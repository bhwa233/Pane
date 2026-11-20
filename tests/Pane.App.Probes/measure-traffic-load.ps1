#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Galaxy','Pane','Harness')][string]$Product,
    [Parameter(Mandatory)][string]$Scenario,
    [Parameter(Mandatory)][string]$LoadExecutable,
    [Parameter(Mandatory)][int[]]$HostProcessId,
    [int]$ServiceProcessId,
    [int[]]$BridgeProcessId = @(),
    [string]$GalaxyStatusBridge,
    [Parameter(Mandatory)][string]$Report
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'traffic-counter-summary.ps1')
$serviceName = switch ($Product) { 'Pane' { 'PaneTraffic' }; 'Galaxy' { 'spark233Traffic' } }
$peerServiceName = switch ($Product) { 'Pane' { 'spark233Traffic' }; 'Galaxy' { 'PaneTraffic' } }
if ($Product -eq 'Galaxy') {
    if (!$GalaxyStatusBridge) { throw 'Supply the Galaxy native traffic client with -GalaxyStatusBridge.' }
    $statusBridge = (Get-Item -LiteralPath $GalaxyStatusBridge).FullName
    if ([IO.Path]::GetFileName($statusBridge) -ine 'spark233.TrafficClient.exe' -or
        !(Test-Path -LiteralPath $statusBridge -PathType Leaf)) { throw 'Use the native spark233.TrafficClient.exe.' }
} elseif ($GalaxyStatusBridge) { throw '-GalaxyStatusBridge is only for Galaxy runs.' }

function Read-RegisteredService {
    $registered = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -Property Name,State,ProcessId
    if ($null -eq $registered -or $registered.State -ne 'Running' -or $registered.ProcessId -le 0) {
        throw "The registered $serviceName service is not running."
    }
    [ordered]@{ name = $registered.Name; pid = [int]$registered.ProcessId }
}

function Read-InactivePeer {
    try { $peer = Get-Service -Name $peerServiceName -ErrorAction Stop }
    catch {
        if ($_.FullyQualifiedErrorId -cne 'NoServiceFoundForGivenName,Microsoft.PowerShell.Commands.GetServiceCommand') { throw }
        return 'NotInstalled'
    }
    if ($peer.Status -ne 'Stopped') {
        throw "The other product's $peerServiceName service is $($peer.Status). Measure in an isolated environment with only the target service running."
    }
    'Stopped'
}

$service = $null
if ($serviceName) {
    $null = Read-InactivePeer
    $service = Read-RegisteredService
    if ($PSBoundParameters.ContainsKey('ServiceProcessId') -and $ServiceProcessId -ne $service.pid) {
        throw "ServiceProcessId does not match the registered $serviceName process."
    }
    $ServiceProcessId = $service.pid
}
$roles = [ordered]@{ host = $HostProcessId; service = @($ServiceProcessId | Where-Object { $_ -ne 0 }); bridge = $BridgeProcessId }
$pids = @(foreach ($role in $roles.Values) { $role })
if ($pids -contains $PID -or ($pids | Where-Object { $_ -le 0 }) -or (@($pids | Sort-Object -Unique).Count -ne $pids.Count)) {
    throw 'Use distinct positive PIDs for measured processes, excluding the sampler.'
}
$exe = (Get-Item -LiteralPath $LoadExecutable).FullName
$assembly = [IO.Path]::ChangeExtension($exe, '.dll')
$reportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Report)
if ((Test-Path -LiteralPath $reportPath) -or !(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($reportPath)) -PathType Container)) {
    throw 'Report must be a new file in an existing directory.'
}
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$assemblyHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
$account = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ($null -eq $account.User) { throw 'Cannot determine the Windows benchmark account.' }
    $sidBytes = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($account.User.Value))
    try { $accountSidSha256 = (Get-FileHash -InputStream $sidBytes -Algorithm SHA256).Hash }
    finally { $sidBytes.Dispose() }
} finally { $account.Dispose() }
$logical = (Get-CimInstance Win32_ComputerSystem -Property NumberOfLogicalProcessors).NumberOfLogicalProcessors
if ($logical -lt 1) { throw 'Cannot determine the number of logical CPUs.' }
$pidFilter = ($pids | ForEach-Object { "IDProcess=$_" }) -join ' OR '
$identityFilter = ($pids | ForEach-Object { "ProcessId=$_" }) -join ' OR '

function Read-CollectionStatus([int]$ExpectedPid) {
    $PSNativeCommandUseErrorActionPreference = $false
    $response = if ($Product -eq 'Galaxy') {
        & $exe --traffic-galaxy-status-json $statusBridge $ExpectedPid
    } else {
        & $exe --traffic-service-status-json $ExpectedPid
    }
    if ($LASTEXITCODE -ne 0) { throw "Cannot read $serviceName collection status (exit $LASTEXITCODE)." }
    $evidence = $response | ConvertFrom-Json
    $status = $evidence.status
    $epoch = [Guid]::Empty
    if ($evidence.servicePid -ne $ExpectedPid -or $status.scope -cne 'own' -or $status.source -cne 'etw' -or
        $status.installed -ne $true -or $status.serviceAvailable -ne $true -or
        $status.enabled -ne $true -or $status.collecting -ne $true -or $status.hasLease -ne $false -or
        ![Guid]::TryParseExact($status.collectorEpoch, 'N', [ref]$epoch) -or $null -eq $status.generation -or
        $null -eq $status.quality) {
        throw "$Product own ETW collection is not active or returned invalid status. Start collection from $Product before measuring."
    }
    $evidence
}

function Read-Identities {
    @(Get-CimInstance Win32_Process -Filter $identityFilter -Property ProcessId,ParentProcessId,Name,CreationDate |
        Sort-Object ProcessId | ForEach-Object { [pscustomobject]@{
            pid = [int]$_.ProcessId; parentPid = [int]$_.ParentProcessId; name = $_.Name
            createdUtc = $_.CreationDate.ToUniversalTime().ToString('O')
        } })
}

function Read-Sample {
    $rows = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter $pidFilter -Property IDProcess,Timestamp_Sys100NS,PercentProcessorTime,WorkingSetPrivate,PrivateBytes,HandleCount)
    $times = @($rows.Timestamp_Sys100NS | Sort-Object -Unique)
    if ($rows.Count -ne $pids.Count -or $times.Count -ne 1) { throw 'Process counter snapshot is missing a PID or not synchronized.' }
    $processes = @{}
    foreach ($row in $rows) {
        $processes[[int]$row.IDProcess] = [pscustomobject]@{
            pid = [int]$row.IDProcess; cpu100ns = [long]$row.PercentProcessorTime
            privateWorkingSet = [long]$row.WorkingSetPrivate; privateBytes = [long]$row.PrivateBytes; handles = [int]$row.HandleCount
        }
    }
    if ($processes.Count -ne $pids.Count) { throw 'Process counter snapshot repeated a PID.' }
    [pscustomobject]@{ time100ns = [long]$times[0]; utc = [DateTimeOffset]::FromFileTime($times[0]).UtcDateTime.ToString('O'); processes = @($pids | ForEach-Object { $processes[$_] }) }
}

$identities = Read-Identities
if ($identities.Count -ne $pids.Count) { throw 'Some measured PIDs are absent before the run.' }
$null = Read-Sample
$beforeCollection = if ($service) { Read-CollectionStatus $ServiceProcessId }
$beforePeer = if ($service) {
    [ordered]@{ state = Read-InactivePeer; observedUtc = [DateTimeOffset]::UtcNow }
}
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--traffic-loopback-load-json')
$load = [Diagnostics.Process]::Start($start)
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $loadCreated = $load.StartTime.ToUniversalTime().ToFileTimeUtc()
    $stdout = $load.StandardOutput.ReadToEndAsync()
    $stderr = $load.StandardError.ReadToEndAsync()
    $frames = [Collections.Generic.List[object]]::new()
    while (!$load.HasExited) {
        if ($watch.Elapsed.TotalSeconds -gt 80) { throw 'The loopback load did not finish in time.' }
        $frame = Read-Sample
        if ($service) { $frame | Add-Member -NotePropertyName peerServiceState -NotePropertyValue (Read-InactivePeer) }
        if ($frames.Count -eq 0 -or $frame.time100ns -gt $frames[-1].time100ns) { $frames.Add($frame) }
        if ($load.WaitForExit(2000)) { break }
    }
    if ($load.ExitCode -ne 0) { throw "Loopback load failed ($($load.ExitCode)): $($stderr.GetAwaiter().GetResult())" }
    $document = [System.Text.Json.JsonDocument]::Parse($stdout.GetAwaiter().GetResult())
    try {
        $data = $document.RootElement
        $loadData = [ordered]@{
            pid = $data.GetProperty('pid').GetInt32(); elapsedMs = $data.GetProperty('elapsedMs').GetInt64()
            createdFileTimeUtc = $loadCreated.ToString([Globalization.CultureInfo]::InvariantCulture)
            txPayloadBytes = $data.GetProperty('txPayloadBytes').GetInt64(); rxPayloadBytes = $data.GetProperty('rxPayloadBytes').GetInt64()
            startUtc = $data.GetProperty('startUtc').GetDateTimeOffset(); endUtc = $data.GetProperty('endUtc').GetDateTimeOffset()
        }
    } finally { $document.Dispose() }
    if ($loadData.pid -ne $load.Id -or $loadData.elapsedMs -lt 58000 -or $loadData.elapsedMs -gt 63000 -or
        $loadData.txPayloadBytes -ne 19660800 -or $loadData.rxPayloadBytes -ne 19660800) { throw 'The load did not match the fixed benchmark.' }
} finally {
    if (!$load.HasExited) { $load.Kill(); $load.WaitForExit() }
    $load.Dispose()
}

if ($service -and (Read-RegisteredService).pid -ne $service.pid) { throw "The registered $serviceName process changed during the run." }
if ((Read-Identities | ConvertTo-Json -Compress) -cne ($identities | ConvertTo-Json -Compress)) { throw 'A measured process changed identity during the run.' }
$afterCollection = if ($service) { Read-CollectionStatus $ServiceProcessId }
$afterPeer = if ($service) {
    [ordered]@{ state = Read-InactivePeer; observedUtc = [DateTimeOffset]::UtcNow }
}
if ($beforeCollection -and ($beforeCollection.status.collectorEpoch -cne $afterCollection.status.collectorEpoch -or
    $beforeCollection.status.generation -ne $afterCollection.status.generation)) {
    throw "$Product collector epoch or own scope generation changed during the run."
}
$from = $loadData.startUtc.ToFileTime()
$to = $loadData.endUtc.ToFileTime()
$samples = @($frames | Where-Object { $_.time100ns -ge $from -and $_.time100ns -le $to })
if ([Math]::Abs(($to - $from) / 10000 - $loadData.elapsedMs) -gt 500) { throw 'The load clock and elapsed time disagree.' }
$summary = Get-TrafficCounterSummary $roles $samples $logical $loadData.startUtc $loadData.endUtc
$span = $samples[-1].time100ns - $samples[0].time100ns
$result = [ordered]@{
    product = $Product; scenario = $Scenario; measurement = 'process-counters-only'; collectionVerified = $false
    machine = $env:COMPUTERNAME; logicalProcessors = $logical; loadExecutableSha256 = $exeHash; loadAssemblySha256 = $assemblyHash; load = $loadData
    accountSidSha256 = $accountSidSha256
    registeredService = $service
    identities = $identities; roles = $roles; measuredSeconds = $span / 1e7; summary = $summary; samples = $samples
}
if ($beforeCollection) {
    $key = if ($Product -eq 'Pane') { 'paneCollectionStatus' } else { 'galaxyCollectionStatus' }
    $result[$key] = [ordered]@{ beforeLoad = $beforeCollection; afterLoad = $afterCollection }
    $result.peerServiceStatus = [ordered]@{ name = $peerServiceName; beforeLoad = $beforePeer; afterLoad = $afterPeer }
}
$result = $result | ConvertTo-Json -Depth 8
$reportStream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($result)
    $reportStream.Write($bytes, 0, $bytes.Length)
} finally { $reportStream.Dispose() }
Write-Host ('{0} {1}: {2} samples, {3:F1}s, total PWS {4:F2} MiB, CPU {5:F3}%; collection not verified. {6}' -f
    $Product,$Scenario,$samples.Count,($span / 1e7),$summary.total.privateWorkingSetMedianMiB,$summary.total.cpuMachinePct,$reportPath)
