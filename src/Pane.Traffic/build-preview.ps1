#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../out/traffic-preview'),
    [switch]$WithSetupPreflight,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$SetupSignerSha256
)

$ErrorActionPreference = 'Stop'
if ($SetupSignerSha256 -and !$WithSetupPreflight) { throw 'SetupSignerSha256 requires WithSetupPreflight.' }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $PSScriptRoot 'Service/Traffic.Service.csproj'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$work = Join-Path $output ('build-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$build = Join-Path $work 'build'
$stage = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null

& dotnet restore $project --locked-mode "-p:TrafficBuildRoot=$build"
if ($LASTEXITCODE) { throw 'Traffic service locked restore failed.' }
& dotnet build $project -c Release --no-restore "-p:TrafficBuildRoot=$build"
if ($LASTEXITCODE) { throw 'Traffic service Release build failed.' }

$binaries = Join-Path $build 'bin/Traffic.Service/Release/net48'
foreach ($required in 'Pane.TrafficService.exe','Pane.TrafficService.exe.config','Pane.Traffic.Core.dll',
    'Microsoft.Diagnostics.Tracing.TraceEvent.dll','amd64/KernelTraceControl.dll','amd64/msdia140.dll',
    'Microsoft.Data.Sqlite.dll','SQLitePCLRaw.batteries_v2.dll','e_sqlite3.dll','runtimes/win-x64/native/e_sqlite3.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $binaries $required) -PathType Leaf)) {
        throw "Traffic service build is missing $required."
    }
}
foreach ($file in Get-ChildItem -LiteralPath $binaries -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($binaries, $file.FullName).Replace('\', '/')
    if ($file.Extension -eq '.pdb' -or $relative.StartsWith('runtimes/win-x86/')) { continue }
    if ($relative -ne 'Pane.TrafficService.exe' -and $file.Extension -notin '.dll','.config') {
        throw "Unexpected traffic service output: $relative"
    }
    $target = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'LICENSE-EXCEPTION.md'),
    (Join-Path $PSScriptRoot 'LICENSE.galaxy-tools.txt'),(Join-Path $PSScriptRoot 'README.md') -Destination $stage
$dependencies = Join-Path $stage 'Dependencies'
New-Item -ItemType Directory -Path $dependencies | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Service/packages.lock.json') -Destination (Join-Path $dependencies 'Traffic.Service.lock.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Core/packages.lock.json') -Destination (Join-Path $dependencies 'Traffic.Core.lock.json')
$assets = Get-Content -LiteralPath (Join-Path $build 'obj/Traffic.Service/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($entry in $assets.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' }) {
    $notices = $entry.Value.files | Where-Object { $_ -match '^(?:license|notice|copying|third[-_. ]party[-_. ]notices)(?:\.[^/]+)?$' }
    if (!$notices) { continue }
    $package = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $entry.Value.path } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (!$package) { throw "Restored package was not found: $($entry.Key)" }
    $target = Join-Path $dependencies $entry.Value.path
    New-Item -ItemType Directory -Path $target | Out-Null
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath (Join-Path $package $notice) -Destination $target
    }
}

$archive = Join-Path $work 'Pane.TrafficService-x64-preview.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
[ordered]@{
    schema = 1
    archive = Split-Path $archive -Leaf
    sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    files = $files
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$archive.manifest.json" -Encoding utf8
Write-Host "Pane Traffic preview: $archive"

if ($WithSetupPreflight) {
    $setup = Join-Path $repo 'src/Pane.Setup/Pane.Setup.csproj'
    $setupOutput = Join-Path $work 'setup-preflight'
    $setupArguments = @('publish', $setup, '-c', 'Release', '-r', 'win-x64', '--artifacts-path',
        (Join-Path $work 'setup-build'), "-p:TrafficPayloadPath=$archive", '-o', $setupOutput)
    if ($SetupSignerSha256) { $setupArguments += "-p:TrafficReleaseSignerSha256=$SetupSignerSha256" }
    & dotnet @setupArguments
    if ($LASTEXITCODE) { throw 'Traffic setup preflight build failed.' }
    $helper = Join-Path $setupOutput 'Pane.TrafficSetup.exe'
    & $helper --verify-payload
    if ($LASTEXITCODE) { throw "Traffic setup preflight rejected its embedded payload: $LASTEXITCODE" }
    Write-Host "Pane Traffic setup preflight: $helper"
}
