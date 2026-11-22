param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$exe = Get-Item -LiteralPath (Join-Path $app 'Pane.App.exe')
$version = $exe.VersionInfo.ProductVersion.Split('+')[0]
Get-Item -LiteralPath (Join-Path $app 'NativeCapture/ScreenCapture.exe') | Out-Null
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$work = New-Item -ItemType Directory -Path (Join-Path $output ("bundle-" + [Guid]::NewGuid().ToString('N')))
try {
    $payload = Join-Path $work.FullName 'payload.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($payload, 'Create')
    try {
        foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object Extension -NE '.pdb') {
            $relative = $file.FullName.Substring($app.TrimEnd('\','/').Length + 1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, 'Optimal') | Out-Null
        }
    }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $payload).Hash
    dotnet publish (Join-Path $repo 'src/Pane.Bundle/Pane.Bundle.csproj') -c Release -r win-x64 `
        "-p:Version=$version" "-p:InformationalVersion=$($exe.VersionInfo.ProductVersion)" `
        '-p:IncludeSourceRevisionInInformationalVersion=false' "-p:PayloadPath=$payload" "-p:PayloadSha256=$hash" -o $work.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Pane single-file build failed.' }
    Copy-Item -LiteralPath (Join-Path $work.FullName 'pane.exe') -Destination $output
    Copy-Item -LiteralPath (Join-Path $app 'release-notes.md') -Destination $output
    $checksum = (Get-FileHash -LiteralPath (Join-Path $output 'pane.exe')).Hash.ToLowerInvariant()
    "$checksum  pane.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
}
finally {
    # The temporary directory was created directly under the selected output root.
    $resolvedWork = (Resolve-Path -LiteralPath $work.FullName).Path
    if ((Split-Path $resolvedWork -Parent) -eq (Resolve-Path -LiteralPath $output).Path) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
