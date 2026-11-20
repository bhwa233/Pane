function Get-TrafficCounterSummary($Roles, [object[]]$Samples, [int]$LogicalProcessors, [DateTimeOffset]$Start, [DateTimeOffset]$End) {
    $groups = [ordered]@{ host = @($Roles.host); service = @($Roles.service); bridge = @($Roles.bridge) }
    $pids = @($groups.host + $groups.service + $groups.bridge)
    if (!$groups.host.Count -or $LogicalProcessors -lt 1 -or @($pids | Where-Object {
        ($_ -isnot [int] -and $_ -isnot [long]) -or $_ -le 0 -or $_ -gt [int]::MaxValue
    }).Count -or @($pids | Sort-Object -Unique).Count -ne $pids.Count) {
        throw 'Counter roles must contain distinct positive PIDs and at least one host.'
    }
    $groups.total = $pids
    $from = $Start.ToFileTime()
    $to = $End.ToFileTime()
    if ($to -le $from -or $Samples.Count -lt 10 -or $Samples[0].time100ns - $from -gt 5e7 -or
        $to - $Samples[-1].time100ns -gt 5e7) { throw 'Counter samples do not cover the steady-load interval.' }

    $lastTime = $null
    $lastRows = @{}
    $frames = @(foreach ($sample in $Samples) {
        $time = $sample.time100ns
        if ($time -isnot [long] -or $time -lt $from -or $time -gt $to -or
            ($null -ne $lastTime -and ($time -le $lastTime -or $time - $lastTime -gt 8e7))) {
            throw 'Counter sample time is outside the load interval, out of order, or more than eight seconds from its predecessor.'
        }
        $rows = @{}
        foreach ($row in $sample.processes) {
            if (($row.pid -isnot [int] -and $row.pid -isnot [long]) -or $row.pid -notin $pids -or $rows.ContainsKey([int]$row.pid)) {
                throw 'Counter sample includes an unexpected or duplicate PID.'
            }
            $processId = [int]$row.pid
            foreach ($field in 'cpu100ns', 'privateWorkingSet', 'privateBytes') {
                $value = $row.$field
                if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0) {
                    throw "Invalid process counter $field for PID $processId."
                }
            }
            if ($lastRows.ContainsKey($processId) -and $row.cpu100ns -lt $lastRows[$processId].cpu100ns) {
                throw "CPU counter moved backwards for PID $processId."
            }
            $rows[$processId] = $row
        }
        if ($rows.Count -ne $pids.Count) { throw 'Counter sample is missing a measured PID.' }
        $lastTime = $time
        $lastRows = $rows
        $rows
    })

    $span = $Samples[-1].time100ns - $Samples[0].time100ns
    $summary = [ordered]@{}
    foreach ($group in $groups.GetEnumerator()) {
        if (!$group.Value.Count) { continue }
        $privateSets = @($frames | ForEach-Object {
            $frame = $_
            ($group.Value | ForEach-Object { $frame[[int]$_].privateWorkingSet } | Measure-Object -Sum).Sum
        } | Sort-Object)
        $privateCommits = @($frames | ForEach-Object {
            $frame = $_
            ($group.Value | ForEach-Object { $frame[[int]$_].privateBytes } | Measure-Object -Sum).Sum
        } | Sort-Object)
        $cpu = ($group.Value | ForEach-Object { $frames[-1][[int]$_].cpu100ns - $frames[0][[int]$_].cpu100ns } | Measure-Object -Sum).Sum
        $lower = ($Samples.Count - 1) -shr 1
        $upper = $Samples.Count -shr 1
        $summary[$group.Key] = [ordered]@{
            privateWorkingSetMedianMiB = ($privateSets[$lower] + $privateSets[$upper]) / 2MB
            privateCommitMedianMiB = ($privateCommits[$lower] + $privateCommits[$upper]) / 2MB
            cpuMachinePct = 100 * $cpu / $span / $LogicalProcessors
        }
    }
    $summary
}
