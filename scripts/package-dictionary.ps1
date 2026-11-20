param([Parameter(Mandatory)][string]$SourceDirectory, [string]$OutputRoot)
$ErrorActionPreference = 'Stop'

if (!$OutputRoot) { $OutputRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'out/dictionary-pack' }
$files = [ordered]@{
    'ecdict.sqlite' = 'd7c046f2ba43d2630cf276fb5b790f85c8f3d3f26ca12c1b7ef29d5db995a489'
    'ECDICT-LICENSE.txt' = 'f8552dd246f61a4e064569eae6194a01c6b3d63b03bf27c6ca863593c549ed0f'
    'provenance.json' = '8add62e09d4657daa94b2a169b538271132b0d1bce00aca48d4911bf0ea94abd'
}
$work = Join-Path $OutputRoot ('build-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'DictionaryData'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($name in $files.Keys) {
    $staged = Join-Path $stage $name
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $name) -Destination $staged
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $files[$name]) {
        throw "Dictionary asset checksum mismatch: $name"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $work 'Pane-ECDICT-v1.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$checksum  $([IO.Path]::GetFileName($zip))`n")
[ordered]@{ archive = $zip; bytes = (Get-Item -LiteralPath $zip).Length; sha256 = $checksum } | ConvertTo-Json
