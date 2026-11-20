#requires -Version 7.5
param([Parameter(Mandatory)][string]$ServiceExecutable)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/remove-tree.ps1')
$executable = Get-Item -LiteralPath $ServiceExecutable
if ($executable -isnot [IO.FileInfo] -or $executable.Name -ne 'Pane.TrafficService.exe') {
    throw 'Provide the Pane traffic service executable from a preview build.'
}

$temp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$root = [IO.Directory]::CreateTempSubdirectory('Pane-TrafficShutdown-').FullName
if ([IO.Path]::GetDirectoryName($root) -ne $temp) { throw 'Unexpected traffic probe directory.' }
$pipeName = 'pane-traffic-test-' + [Guid]::NewGuid().ToString('N')
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    foreach ($count in 0, 16) {
        $start = [Diagnostics.ProcessStartInfo]::new($executable.FullName)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardError = $true
        foreach ($arg in '--console', '--isolate', (Join-Path $root "clients-$count"), '--pipe', $pipeName) {
            $start.ArgumentList.Add($arg)
        }
        $child = [Diagnostics.Process]::Start($start)
        $clients = [Collections.Generic.List[IO.Pipes.NamedPipeClientStream]]::new()
        try {
            $ready = $child.StandardError.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
            if ($ready -cne 'traffic-isolated-ready') { throw "Traffic preview did not start: $ready" }
            for ($index = 0; $index -lt $count; $index++) {
                $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, 'InOut', 'Asynchronous', 'Impersonation')
                $clients.Add($pipe)
                $reader = [IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
                $writer = [IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
                try {
                    $pipe.Connect(3000)
                    $writer.WriteLine('{"v":1,"id":"stop-check","op":"status"}')
                    $writer.Flush()
                    $response = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult() | ConvertFrom-Json
                    if (!$response.ok -or $response.data.collecting -or $response.data.hasLease) { throw 'Unexpected traffic status.' }
                    if ($index % 2 -eq 0) { $writer.Write('{"v":1,'); $writer.Flush() }
                }
                finally { $writer.Dispose(); $reader.Dispose() }
            }
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $child.StandardInput.Close()
            if (!$child.WaitForExit(2500)) { throw "Traffic preview did not stop within 2.5 seconds with $count open clients." }
            $elapsed = $stopwatch.ElapsedMilliseconds
            $stderr = $child.StandardError.ReadToEnd()
            if ($child.ExitCode -ne 0 -or $stderr) { throw "Traffic preview exited with $($child.ExitCode): $stderr" }
            [pscustomobject]@{ clients = $count; stopElapsedMs = $elapsed; exitedBeforeClientsClosed = $true }
        }
        finally {
            foreach ($pipe in $clients) { $pipe.Dispose() }
            if (!$child.WaitForExit(5000)) { $child.Kill(); $child.WaitForExit() }
            $child.Dispose()
        }
    }
}
finally { Remove-LinkSafeTree $root }
