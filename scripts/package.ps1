param([switch]$NoBuild,[string]$PolicyPath,[switch]$WithNativeCapture,[switch]$IsolatedPreview,[switch]$WithTrafficService,[string]$OcrPreviewSource,[string]$OcrOnnxRuntimeArchive,[string]$PreviewOutputRoot)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if($IsolatedPreview -and (!$WithNativeCapture -or $NoBuild)){throw '隔离预览需要 -WithNativeCapture 和全新构建'}
if($WithTrafficService -and !$IsolatedPreview){throw '流量服务载荷仅允许进入隔离预览包'}
if($OcrPreviewSource -and !$IsolatedPreview){throw 'OCR 载荷仅允许进入隔离预览包'}
if($OcrOnnxRuntimeArchive -and !$OcrPreviewSource){throw '从源码打包 OCR 需要 -OcrPreviewSource 提供 VC 运行时及许可文件'}
if($PreviewOutputRoot -and !$IsolatedPreview){throw '自定义预览输出目录仅支持隔离预览包'}
if($PreviewOutputRoot -and !(Test-Path -LiteralPath $PreviewOutputRoot -PathType Container)){throw '预览输出目录必须已存在'}
$dist=Join-Path $repo $(if($WithNativeCapture){'out/capture-preview'}else{'dist'})
if($PreviewOutputRoot){
    $previewRoot=Resolve-Path -LiteralPath $PreviewOutputRoot
    if($previewRoot.Provider.Name -ne 'FileSystem'){throw '预览输出目录必须位于文件系统'}
    $dist=$previewRoot.ProviderPath
}
if($IsolatedPreview){$dist=Join-Path $dist ('run-'+[Guid]::NewGuid().ToString('N'))}
. (Join-Path $PSScriptRoot 'forbidden-scan.ps1')
# 公开副本不携带内部政策；在副本打包时须显式提供政策文件。
if(!$PolicyPath){$PolicyPath=Join-Path $repo 'docs/internal.md'}
$rules=@(Read-ForbiddenRules $PolicyPath)
$dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
. (Join-Path $PSScriptRoot 'remove-tree.ps1')
$setupBin=Join-Path $repo 'src/Pane.Setup/bin'
$work=if($IsolatedPreview){Join-Path $dist 'work'}else{Join-Path $setupBin ('package-'+[Guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Path $work -Force | Out-Null
# 失败时整个临时目录留作现场，写明原因；成功打包时只保留最近 3 次失败。
trap {
    if($work -and (Test-Path -LiteralPath $work)){
        [IO.File]::WriteAllText((Join-Path $work 'FAILED.txt'),"$([DateTime]::Now.ToString('s'))`r`n$($_ | Out-String)",[Text.UTF8Encoding]::new($false))
        Write-Host "打包失败，现场保留在：$work"
    }
    break
}
$stage=Join-Path $work 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null
if($OcrPreviewSource){
    $ocrAssets=Import-PowerShellDataFile -LiteralPath (Join-Path $repo 'src/Pane.Capture/ocr-preview.lock.psd1')
    foreach($path in $ocrAssets.Keys){
        $notice=$path.StartsWith('legal/') -or $path -eq 'THIRD_PARTY_NOTICES.md'
        if($OcrOnnxRuntimeArchive -and !$notice){continue}
        $relative=if($notice){$path}else{"native/win32-x64/$path"}
        $target=Join-Path $stage "NativeOcr/$path"
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $OcrPreviewSource $relative) -Destination $target
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $ocrAssets[$path]){throw "OCR 预览文件哈希不匹配：$path"}
    }
}
if($NoBuild){
    $last=Join-Path $repo 'dist/package.json'
    $source=if(Test-Path -LiteralPath $last){(Get-Content -LiteralPath $last -Encoding UTF8 -Raw | ConvertFrom-Json).source}else{Join-Path $repo 'src/Pane.App/bin/aot'}
}
else {
    # 每次发布到新目录，防止旧元包留下的 SDK 文件混入组件包输出。
    & "$repo/src/Pane.App/scripts/build.ps1" -Mode aot -OutputRoot "$work/app-build" -IsolatedPreview:$IsolatedPreview
    if($LASTEXITCODE -ne 0){throw '应用构建失败'}
    $source=Join-Path $work 'app-build/aot'
}
if(!(Test-Path -LiteralPath "$source/Pane.App.exe")){throw '缺少 AOT 构建'}
$v=[Diagnostics.FileVersionInfo]::GetVersionInfo("$source/Pane.App.exe")
$version="$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)"
foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
    if($file.Extension -eq '.pdb' -or $file.Name -eq 'release-notes.md'){continue}
    $relative=$file.FullName.Substring($source.TrimEnd('\').Length+1)
    if($relative -match '^(NativeCapture|NativeOcr|TrafficService)[\\/]'){continue}
    $target=Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
if($WithNativeCapture){
    $pwsh=(Get-Command pwsh.exe -ErrorAction Stop).Source
    & $pwsh -NoProfile -File "$repo/src/Pane.Capture/build.ps1" -OutputRoot "$work/capture-build" -StageDirectory "$stage/NativeCapture"
    if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath "$stage/NativeCapture/ScreenCapture.exe")){throw '原生截图构建失败'}
    # 子构建产物立即扫描，避免最后才在打包扫描发现嵌入路径。
    Assert-ForbiddenFiles -Files @(Get-ChildItem -LiteralPath "$stage/NativeCapture" -Recurse -File|ForEach-Object {$_.FullName}) -Rules $rules
}
if($OcrOnnxRuntimeArchive){
    $ocrRoot=Join-Path $work 'ocr-build'
    $vcRuntime=Join-Path $OcrPreviewSource 'native/win32-x64'
    & $pwsh -NoProfile -File "$repo/src/Pane.Capture/build.ps1" -OutputRoot $ocrRoot -BuildOcr -OnnxRuntimeArchive $OcrOnnxRuntimeArchive -VcRuntimeDirectory $vcRuntime
    if($LASTEXITCODE -ne 0){throw 'OCR 源码构建失败'}
    $ocrBuilds=@(Get-ChildItem -LiteralPath $ocrRoot -Directory -Filter 'build-*')
    if($ocrBuilds.Count -ne 1){throw '未能确定 OCR 源码构建目录'}
    # 子构建产物立即扫描，避免最后才在打包扫描发现嵌入路径。
    Assert-ForbiddenFiles -Files @(Get-ChildItem -LiteralPath (Join-Path $ocrBuilds[0].FullName 'NativeOcr') -Recurse -File|ForEach-Object {$_.FullName}) -Rules $rules
    foreach($name in $ocrAssets.Keys | Where-Object {$_ -like '*.dll' -or $_ -eq 'ImageReader.exe'}){
        Copy-Item -LiteralPath (Join-Path $ocrBuilds[0].FullName "NativeOcr/$name") -Destination "$stage/NativeOcr"
    }
    Copy-Item -LiteralPath (Join-Path $ocrBuilds[0].FullName 'NativeOcr/sources.lock.json') -Destination "$stage/NativeOcr"
}
if($WithTrafficService){
    $trafficRoot=Join-Path $work 'traffic-build'
    & $pwsh -NoProfile -File "$repo/src/Pane.Traffic/build-preview.ps1" -OutputRoot $trafficRoot -WithSetupPreflight
    if($LASTEXITCODE -ne 0){throw '流量服务预览构建失败'}
    $trafficBuilds=@(Get-ChildItem -LiteralPath $trafficRoot -Directory -Filter 'build-*')
    if($trafficBuilds.Count -ne 1){throw '未能确定流量服务预览构建目录'}
    $trafficBuild=$trafficBuilds[0].FullName
    $trafficZip=Join-Path $trafficBuild 'Pane.TrafficService-x64-preview.zip'
    $trafficHelper=Join-Path $trafficBuild 'setup-preflight/Pane.TrafficSetup.exe'
    Assert-ForbiddenFiles -Files (@($trafficHelper)+@(Get-ChildItem -LiteralPath (Join-Path $trafficBuild 'payload') -Recurse -File | ForEach-Object {$_.FullName})) -Rules $rules
    $target=Join-Path $stage 'TrafficService'
    New-Item -ItemType Directory -Path $target | Out-Null
    Copy-Item -LiteralPath $trafficZip,"$trafficZip.manifest.json" -Destination $target
}
foreach($notice in @('LICENSE','LICENSE-EXCEPTION.md','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $stage}
Copy-Item -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty') -Destination $stage -Recurse
$setupProperties=@()
if($IsolatedPreview){$setupProperties+='-p:PaneIsolatedPreview=true'}
# 小助手不嵌入 payload；卸载时负责搬移应用、等待退出及清理临时应用副本。
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Pane.Maintenance','-o',"$work/helper") @setupProperties
if($LASTEXITCODE -ne 0){throw '卸载助手构建失败'}
Copy-Item -LiteralPath "$work/helper/Pane.Maintenance.exe" -Destination $stage
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Uninstall Pane','-o',"$work/uninstaller") @setupProperties
if($LASTEXITCODE -ne 0){throw '独立卸载程序构建失败'}
Copy-Item -LiteralPath "$work/uninstaller/Uninstall Pane.exe" -Destination $stage
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
& $dotnet @('publish',"$repo/src/Pane.Setup/Pane.Setup.csproj",'-c','Release','-r','win-x64',"-p:Version=$version",'-p:AssemblyName=Pane.Setup',"-p:PayloadPath=$zip",'-o',"$work/wrapper") @setupProperties
if($LASTEXITCODE -ne 0){throw '安装外壳构建失败'}
# 扫描安装外壳及未压缩 payload，避免压缩掩盖泄漏；通过后才写发布目录。
Assert-ForbiddenFiles -Files (@("$work/wrapper/Pane.Setup.exe")+@(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {$_.FullName})) -Rules $rules
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'release-notes.md') -Destination (Join-Path $dist 'release-notes.md') -Force
Assert-ForbiddenFiles -Files @((Join-Path $dist 'release-notes.md')) -Rules $rules
$installer=Join-Path $dist $(if($IsolatedPreview){"Pane 隔离预览 $version.exe"}else{"Pane 安装 $version.exe"})
Copy-Item -LiteralPath "$work/wrapper/Pane.Setup.exe" -Destination $installer -Force
$releaseInstaller=Join-Path $dist $(if($IsolatedPreview){"Pane-Preview-$version.exe"}else{"Pane-Setup-$version.exe"})
Copy-Item -LiteralPath $installer -Destination $releaseInstaller -Force
$checksum=(Get-FileHash -LiteralPath $releaseInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
$checksums=@("$checksum  $([IO.Path]::GetFileName($releaseInstaller))")
if($WithTrafficService){
    $trafficPreflight='traffic-preflight/Pane.TrafficSetup.exe'
    $trafficPreflightPath=Join-Path $dist $trafficPreflight
    New-Item -ItemType Directory -Path (Split-Path $trafficPreflightPath -Parent) | Out-Null
    Copy-Item -LiteralPath $trafficHelper -Destination $trafficPreflightPath
    $trafficChecksum=(Get-FileHash -LiteralPath $trafficPreflightPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums+="$trafficChecksum  $trafficPreflight"
}
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'),($checksums -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
$raw=Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum
Assert-ForbiddenFiles -Files @($installer,$releaseInstaller,(Join-Path $dist 'SHA256SUMS.txt')) -Rules $rules
$payload=Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum
$result=[ordered]@{version=$version;source=$source;stage=$stage;installer=$installer;rawFiles=$raw.Count;rawBytes=$raw.Sum;files=$payload.Count;bytes=$payload.Sum;compressedBytes=(Get-Item -LiteralPath $zip).Length;installerBytes=(Get-Item -LiteralPath $installer).Length}
if($IsolatedPreview){$result.isolatedPreview=$true}
if($WithTrafficService){$result.trafficPreflight=$trafficPreflight}
if($OcrPreviewSource){$result.ocrPreview=$true}
if($OcrOnnxRuntimeArchive){$result.ocrSourceBuild=$true}
$result | ConvertTo-Json | Set-Content -LiteralPath "$dist/package.json" -Encoding UTF8
if($IsolatedPreview){$result | ConvertTo-Json;return}
# 成功后删掉其他打包临时目录（每个约 0.2～0.3 GB）。保留本次（package.json 指向它）、-NoBuild 用到的来源、
# 最近 3 次失败的现场，以及 2 小时内新建的（可能有另一次打包正在跑）。
$keep=@([IO.Path]::GetFullPath($work).TrimEnd('\'))
if($source -match '[\\/](package-[0-9a-f]{32})[\\/]'){$keep+=[IO.Path]::GetFullPath((Join-Path $setupBin $Matches[1])).TrimEnd('\')}
$packages=@(Get-ChildItem -LiteralPath $setupBin -Directory -Filter 'package-*')
$keep+=@($packages | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'FAILED.txt')} | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {$_.FullName.TrimEnd('\')})
foreach($old in $packages){
    if($keep -contains $old.FullName.TrimEnd('\') -or $old.CreationTime -gt [DateTime]::Now.AddHours(-2)){continue}
    try {Remove-LinkSafeTree $old.FullName}catch{Write-Warning "旧打包目录没删掉：$($old.FullName)：$($_.Exception.Message)"}
}
$result | ConvertTo-Json
# 017 将仓库外可执行文件统一认作正式版；未扩展该身份规则前不输出会误用正式数据的绿色版。
