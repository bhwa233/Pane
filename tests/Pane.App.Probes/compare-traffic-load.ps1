#requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$GalaxyReport,
    [Parameter(Mandatory)][string[]]$PaneReport,
    [string[]]$GalaxyHistory,
    [string[]]$PaneHistory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'traffic-counter-summary.ps1')

function Read-Report([string]$Path, [string]$Product, [string]$Service) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).ProviderPath)
    $report = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($report.product -cne $Product -or $report.measurement -cne 'process-counters-only' -or
        $report.registeredService.name -cne $Service -or $report.registeredService.pid -le 0 -or
        @($report.roles.service).Count -ne 1 -or $report.roles.service[0] -ne $report.registeredService.pid -or
        [string]::IsNullOrWhiteSpace($report.machine) -or [string]::IsNullOrWhiteSpace($report.scenario) -or
        $report.logicalProcessors -lt 1 -or $report.loadExecutableSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $report.loadAssemblySha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $report.load.txPayloadBytes -ne 19660800 -or $report.load.rxPayloadBytes -ne 19660800 -or
        $report.load.elapsedMs -lt 58000 -or $report.load.elapsedMs -gt 63000 -or $report.samples.Count -lt 10) {
        throw "Not a $Product counter report from the fixed workload and registered $Service service: $Path"
    }
    $account = $report['accountSidSha256']
    if ($report.Contains('accountSidSha256') -and ($account -isnot [string] -or $account -cnotmatch '\A[0-9A-F]{64}\z')) {
        throw "Invalid Windows benchmark account fingerprint: $Path"
    }
    $start = [DateTimeOffset]$report.load.startUtc
    $end = [DateTimeOffset]$report.load.endUtc
    if ([Math]::Abs(($end - $start).TotalMilliseconds - $report.load.elapsedMs) -gt 500) {
        throw "The reported load interval and elapsed time disagree: $Path"
    }
    try { $report.summary = Get-TrafficCounterSummary $report.roles $report.samples $report.logicalProcessors $start $end }
    catch { throw "Invalid $Product process counter samples in ${Path}: $($_.Exception.Message)" }
    $report.reportSha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $report
}

function Assert-PersistedHistory($Report, [string]$Path) {
    $history = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if ($history.persistedHistoryVerified -isnot [bool] -or !$history.persistedHistoryVerified -or
        $history.product -cne $Report.product -or $history.reportSha256 -cne $Report.reportSha256 -or
        $history.servicePid -ne $Report.registeredService.pid -or $history.pid -ne $Report.load.pid -or
        $history.processInstanceId -cnotmatch '^[0-9a-fA-F]{32}:[0-9]+:[0-9]+$' -or
        !$history.processInstanceId.EndsWith(":$($Report.load.pid):$($Report.load.createdFileTimeUtc)", [StringComparison]::Ordinal) -or
        ([DateTimeOffset]$history.startUtc).ToUnixTimeMilliseconds() -ne ([DateTimeOffset]$Report.load.startUtc).ToUnixTimeMilliseconds() -or
        ([DateTimeOffset]$history.endUtc).ToUnixTimeMilliseconds() -ne ([DateTimeOffset]$Report.load.endUtc).ToUnixTimeMilliseconds() -or
        [decimal]$history.recordedRxBytes -lt $Report.load.rxPayloadBytes -or [decimal]$history.recordedTxBytes -lt $Report.load.txPayloadBytes) {
        throw "History evidence does not match the $($Report.product) counter report: $Path"
    }
}

function Saved-Percent([double]$Baseline, [double]$Current) {
    if ($Baseline -gt 0) { return 100 * ($Baseline - $Current) / $Baseline }
    $null
}

function Assert-SameWorkload($Reference, $Candidate) {
    foreach ($key in 'machine', 'scenario', 'logicalProcessors', 'loadExecutableSha256', 'loadAssemblySha256') {
        if ($Reference[$key] -cne $Candidate[$key]) { throw "Reports differ in $key." }
    }
}

function Read-PeerServiceCheck($Report, [string]$Name, [DateTimeOffset]$Start, [DateTimeOffset]$End) {
    if (!$Report.Contains('peerServiceStatus')) { return 'missing' }
    $peer = $Report.peerServiceStatus
    $inactive = 'Stopped', 'NotInstalled'
    if ($peer.name -cne $Name -or $peer.beforeLoad.state -cnotin $inactive -or $peer.afterLoad.state -cnotin $inactive -or
        [string]::IsNullOrWhiteSpace($peer.beforeLoad.observedUtc) -or [string]::IsNullOrWhiteSpace($peer.afterLoad.observedUtc) -or
        [DateTimeOffset]$peer.beforeLoad.observedUtc -gt $Start -or [DateTimeOffset]$peer.afterLoad.observedUtc -lt $End) {
        throw "$($Report.product) report does not show the other product's $Name service inactive at both load endpoints."
    }
    $from = $Start.ToFileTime()
    $to = $End.ToFileTime()
    $previous = $null
    foreach ($sample in $Report.samples) {
        $time = 0L
        if ($sample['peerServiceState'] -cnotin $inactive -or ![long]::TryParse($sample['time100ns'], [ref]$time) -or
            $time -lt $from -or $time -gt $to -or
            ($null -eq $previous -and $time - $from -gt 5e7) -or
            ($null -ne $previous -and ($time -le $previous -or $time - $previous -gt 8e7))) {
            throw "$($Report.product) report has an invalid or missing inactive $Name service sample."
        }
        $previous = $time
    }
    if ($to - $previous -gt 5e7) { throw "$($Report.product) report has no $Name service sample near the end of the load." }
    'inactive-at-samples'
}

function Read-CollectorDiagnostics($Report, [string]$Key, [DateTimeOffset]$Start, [DateTimeOffset]$End) {
    if (!$Report.Contains($Key)) { return [ordered]@{ state = 'missing' } }
    $before = $Report[$Key].beforeLoad
    $after = $Report[$Key].afterLoad
    if ([DateTimeOffset]$before.observedUtc -gt $Start -or [DateTimeOffset]$after.observedUtc -lt $End -or
        $before.status.collectorEpoch -cne $after.status.collectorEpoch -or $before.status.generation -ne $after.status.generation) {
        throw "$($Report.product) collector status does not bracket the load with a stable epoch and generation."
    }
    foreach ($snapshot in $before, $after) {
        $status = $snapshot.status
        $epoch = [Guid]::Empty
        if ($null -eq $snapshot.observedUtc -or $snapshot.servicePid -ne $Report.registeredService.pid -or
            $status.scope -cne 'own' -or $status.source -cne 'etw' -or
            $status.enabled -isnot [bool] -or !$status.enabled -or $status.collecting -isnot [bool] -or !$status.collecting -or
            $status.hasLease -isnot [bool] -or $status.hasLease -or
            ![Guid]::TryParseExact($status.collectorEpoch, 'N', [ref]$epoch) -or $null -eq $status.generation -or $status.generation -lt 0 -or
            $status.quality.state -cnotin 'good', 'partial', 'unavailable') {
            throw "Invalid $($Report.product) collector status evidence."
        }
    }

    $delta = [ordered]@{}
    foreach ($key in 'eventsLost', 'realtimeBuffersLost', 'queueDropped', 'parseErrors', 'identityUnresolved') {
        $startValue = 0L
        $endValue = 0L
        if (![long]::TryParse($before.status.quality[$key], [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$startValue) -or
            ![long]::TryParse($after.status.quality[$key], [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$endValue)) {
            throw "Invalid $($Report.product) collector counter: $key."
        }
        $delta[$key] = $endValue - $startValue
    }
    $state = if (@($delta.Values | Where-Object { $_ -lt 0 }).Count) { 'counter-reset' }
        elseif ($delta.eventsLost -gt 0 -or $delta.realtimeBuffersLost -gt 0 -or $delta.queueDropped -gt 0 -or $delta.parseErrors -gt 0) { 'loss-observed' }
        elseif ($delta.identityUnresolved -gt 0) { 'unresolved-identity' }
        elseif ($before.status.quality.state -cne 'good' -or $after.status.quality.state -cne 'good') { 'degraded-endpoints' }
        else { 'no-loss-observed' }
    [ordered]@{ state = $state; beforeQuality = $before.status.quality.state; afterQuality = $after.status.quality.state; delta = $delta }
}

function Compare-Round($Galaxy, $Pane) {
    $comparison = [ordered]@{}
    foreach ($role in 'host', 'service', 'bridge', 'total') {
        $baseline = $Galaxy.summary[$role]
        $current = $Pane.summary[$role]
        if ($role -ne 'bridge' -and ($null -eq $baseline -or $null -eq $current)) { throw "Missing $role counters." }
        if ($null -eq $baseline -and $null -eq $current) { continue }
        $row = [ordered]@{ galaxy = $baseline; pane = $current }
        if ($null -ne $baseline -and $null -ne $current) {
            $row.workingSetSavedMiB = $baseline.privateWorkingSetMedianMiB - $current.privateWorkingSetMedianMiB
            $row.workingSetSavedPct = Saved-Percent $baseline.privateWorkingSetMedianMiB $current.privateWorkingSetMedianMiB
            $row.commitSavedMiB = $baseline.privateCommitMedianMiB - $current.privateCommitMedianMiB
            $row.commitSavedPct = Saved-Percent $baseline.privateCommitMedianMiB $current.privateCommitMedianMiB
            $row.cpuSavedPercentagePoints = $baseline.cpuMachinePct - $current.cpuMachinePct
            $row.cpuSavedPct = Saved-Percent $baseline.cpuMachinePct $current.cpuMachinePct
        }
        $comparison[$role] = $row
    }
    $comparison
}

function Describe-Values([double[]]$Values) {
    [Array]::Sort($Values)
    $middle = $Values.Count -shr 1
    $median = if ($Values.Count % 2 -eq 0) { ($Values[$middle - 1] + $Values[$middle]) / 2 } else { $Values[$middle] }
    [ordered]@{ median = $median; min = $Values[0]; max = $Values[-1] }
}

if ($GalaxyReport.Count -ne $PaneReport.Count -or ($GalaxyReport.Count -ne 1 -and $GalaxyReport.Count -lt 3)) {
    throw 'Supply one pair or at least three pairs, with equal report counts.'
}
$hasGalaxyHistory = $PSBoundParameters.ContainsKey('GalaxyHistory')
$hasPaneHistory = $PSBoundParameters.ContainsKey('PaneHistory')
$withHistory = $hasGalaxyHistory -and $hasPaneHistory
if ($hasGalaxyHistory -ne $hasPaneHistory -or ($withHistory -and ($GalaxyHistory.Count -ne $GalaxyReport.Count -or $PaneHistory.Count -ne $PaneReport.Count))) {
    throw 'Supply one history evidence file per report for both products, in the same order.'
}

$reference = $null
$accountReference = $null
$firstProduct = $null
$previousEnd = $null
$rounds = @(for ($index = 0; $index -lt $GalaxyReport.Count; $index++) {
    $galaxy = Read-Report $GalaxyReport[$index] 'Galaxy' 'spark233Traffic'
    $pane = Read-Report $PaneReport[$index] 'Pane' 'PaneTraffic'
    if ($index -eq 0) { $reference = $galaxy }
    Assert-SameWorkload $reference $galaxy
    Assert-SameWorkload $reference $pane
    foreach ($account in $galaxy['accountSidSha256'], $pane['accountSidSha256']) {
        if ($null -eq $account) { continue }
        if ($null -eq $accountReference) { $accountReference = $account }
        elseif ($accountReference -cne $account) { throw 'Reports were sampled under different Windows benchmark accounts.' }
    }
    $sameAccount = $galaxy.Contains('accountSidSha256') -and $pane.Contains('accountSidSha256')
    $galaxyStart = [DateTimeOffset]$galaxy.load.startUtc
    $galaxyEnd = [DateTimeOffset]$galaxy.load.endUtc
    $paneStart = [DateTimeOffset]$pane.load.startUtc
    $paneEnd = [DateTimeOffset]$pane.load.endUtc
    if ($galaxyEnd -le $galaxyStart -or $paneEnd -le $paneStart -or ($galaxyStart -lt $paneEnd -and $paneStart -lt $galaxyEnd)) {
        throw 'Load intervals must be valid and non-overlapping.'
    }

    $startsWith = if ($galaxyStart -lt $paneStart) { 'Galaxy' } else { 'Pane' }
    if ($index -eq 0) { $firstProduct = $startsWith }
    $start = if ($startsWith -eq 'Galaxy') { $galaxyStart } else { $paneStart }
    $end = if ($startsWith -eq 'Galaxy') { $paneEnd } else { $galaxyEnd }
    if ($startsWith -ne $firstProduct -or ($null -ne $previousEnd -and $start -lt $previousEnd)) {
        throw 'Pairs must be in chronological order, without overlap, and runs must alternate products.'
    }
    $previousEnd = $end

    $comparison = Compare-Round $galaxy $pane
    $galaxyDiagnostics = Read-CollectorDiagnostics $galaxy 'galaxyCollectionStatus' $galaxyStart $galaxyEnd
    $paneDiagnostics = Read-CollectorDiagnostics $pane 'paneCollectionStatus' $paneStart $paneEnd
    $galaxyPeer = Read-PeerServiceCheck $galaxy 'PaneTraffic' $galaxyStart $galaxyEnd
    $panePeer = Read-PeerServiceCheck $pane 'spark233Traffic' $paneStart $paneEnd
    $peerChecksPassed = $galaxyPeer -ceq 'inactive-at-samples' -and $panePeer -ceq 'inactive-at-samples'
    if ($withHistory) {
        Assert-PersistedHistory $galaxy $GalaxyHistory[$index]
        Assert-PersistedHistory $pane $PaneHistory[$index]
    }
    [ordered]@{
        galaxyReport = (Get-Item -LiteralPath $GalaxyReport[$index]).FullName
        paneReport = (Get-Item -LiteralPath $PaneReport[$index]).FullName
        countersMeetWorkingSetThreshold = $comparison.total.workingSetSavedMiB -ge 100 -and $comparison.total.workingSetSavedPct -ge 30
        galaxyCollectorDiagnostics = $galaxyDiagnostics
        paneCollectorDiagnostics = $paneDiagnostics
        peerServiceChecksPassed = $peerChecksPassed
        windowsAccountVerified = $sameAccount
        persistedHistoryVerified = $withHistory
        automaticTrafficChecksPassed = $sameAccount -and $peerChecksPassed -and $withHistory -and
            $galaxyDiagnostics.state -ceq 'no-loss-observed' -and $paneDiagnostics.state -ceq 'no-loss-observed'
        comparison = $comparison
    }
})

$result = [ordered]@{
    measurement = 'counter-comparison-only'
    collectionVerified = $false
    scenario = $reference.scenario
    machine = $reference.machine
}
if ($rounds.Count -eq 1) {
    foreach ($key in $rounds[0].Keys) { $result[$key] = $rounds[0][$key] }
} else {
    $total = [ordered]@{}
    foreach ($metric in 'workingSetSavedMiB', 'workingSetSavedPct', 'commitSavedMiB', 'commitSavedPct', 'cpuSavedPercentagePoints', 'cpuSavedPct') {
        $values = @($rounds | ForEach-Object { $_.comparison.total[$metric] } | Where-Object { $null -ne $_ })
        $total[$metric] = if ($values.Count -eq $rounds.Count) { Describe-Values $values } else { $null }
    }
    $result.roundCount = $rounds.Count
    $result.firstProduct = $firstProduct
    $result.allRoundsMeetWorkingSetThreshold = @($rounds | Where-Object { !$_.countersMeetWorkingSetThreshold }).Count -eq 0
    $result.allRoundsHaveNoObservedPaneCollectorLoss = @($rounds | Where-Object { $_.paneCollectorDiagnostics.state -cne 'no-loss-observed' }).Count -eq 0
    $result.allRoundsHaveNoObservedCollectorLoss = $result.allRoundsHaveNoObservedPaneCollectorLoss -and
        @($rounds | Where-Object { $_.galaxyCollectorDiagnostics.state -cne 'no-loss-observed' }).Count -eq 0
    $result.allRoundsHaveVerifiedPersistedHistory = $withHistory
    $result.allRoundsPassPeerServiceChecks = @($rounds | Where-Object { !$_.peerServiceChecksPassed }).Count -eq 0
    $result.allRoundsMatchWindowsAccount = @($rounds | Where-Object { !$_.windowsAccountVerified }).Count -eq 0
    $result.allRoundsPassAutomaticTrafficChecks = @($rounds | Where-Object { !$_.automaticTrafficChecksPassed }).Count -eq 0
    $result.totalPairedSavings = $total
    $result.rounds = $rounds
}
$result | ConvertTo-Json -Depth 12
