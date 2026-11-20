param([string]$PackageMetadata,[string]$Report,[string]$EngineAssetDirectory,[switch]$WithNativeCapture,[switch]$WithTrafficService,[switch]$WithNativeOcr)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$engineAssets=@('google','bing','bilibili','github','baidu') | ForEach-Object {"Assets/Engines/$_.png"}
# 固定设计确认的 favicon.ico 最大帧，防止源文件和发布文件一起退回旧放大镜。
$bingHash='688789CF2A94D0238659CC1CA40A4EF6AEF8B6585769AD397EEED52102B879C0'
if((Get-FileHash -LiteralPath (Join-Path $repo 'src/Pane.App/Assets/Engines/bing.png')).Hash -ne $bingHash){throw '必应图标不是已核对的 b 标志'}
# 仅构建验收时核对发布目录；完整安装包审计仍执行下方全部检查。
if($EngineAssetDirectory){
    foreach($path in $engineAssets){
        if((Get-FileHash -LiteralPath (Join-Path $repo "src/Pane.App/$path")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $EngineAssetDirectory $path)).Hash){throw "引擎图标不一致：$path"}
    }
    Write-Host "engineAssets=5; hashesVerified=True"
    return
}
if($WithTrafficService -and (!$WithNativeCapture -or !$PackageMetadata -or !$Report)){throw '流量服务审计需要截图开关、隔离预览清单和独立报告路径'}
if($WithNativeOcr -and (!$WithNativeCapture -or !$PackageMetadata -or !$Report)){throw 'OCR 审计需要截图开关、隔离预览清单和独立报告路径'}
if(!$PackageMetadata){$PackageMetadata=Join-Path $repo $(if($WithNativeCapture){'out/capture-preview/package.json'}else{'dist/package.json'})}
if(!$Report){$Report=Join-Path $repo $(if($WithNativeCapture){'out/capture-preview/payload-audit.json'}else{'out/payload-audit.json'})}
$metadata=Get-Content -LiteralPath $PackageMetadata -Encoding UTF8 -Raw | ConvertFrom-Json
if([bool]$metadata.ocrPreview -ne [bool]$WithNativeOcr -or ($WithNativeOcr -and !$metadata.isolatedPreview) -or ($metadata.ocrSourceBuild -and !$WithNativeOcr)){throw 'OCR 预览清单与审计选项不一致'}
$ocrAssets=@{}
if($WithNativeOcr){$ocrAssets=Import-PowerShellDataFile -LiteralPath (Join-Path $repo 'src/Pane.Capture/ocr-preview.lock.psd1')}
if($metadata.ocrSourceBuild){
    $ocrRoot=Join-Path (Split-Path $metadata.stage -Parent) 'ocr-build'
    $builds=@(Get-ChildItem -LiteralPath $ocrRoot -Directory -Filter 'build-*')
    if($builds.Count -ne 1){throw '未能确定包内 OCR 对应的源码构建目录'}
    $ocrAssets['ImageReader.exe']=(Get-FileHash -LiteralPath (Join-Path $builds[0].FullName 'obj/ImageReader.exe') -Algorithm SHA256).Hash
    $ocrAssets.Remove('resources.json')
    $ocrAssets['sources.lock.json']=(Get-FileHash -LiteralPath (Join-Path $repo 'src/Pane.Capture/sources.lock.json') -Algorithm SHA256).Hash
}
$captureAssets=@{}
$trafficAssets=@{}
if($WithNativeCapture){
    $captureRoot=Join-Path (Split-Path $metadata.stage -Parent) 'capture-build'
    $builds=@(Get-ChildItem -LiteralPath $captureRoot -Directory -Filter 'build-*')
    if($builds.Count -ne 1){throw '未能确定预览包的原生截图构建目录'}
    $build=$builds[0].FullName
    $captureAssets=@{
        'NativeCapture/ScreenCapture.exe'=Join-Path $build 'obj/ScreenCapture.exe'
        'NativeCapture/LICENSE.ScreenCapture.txt'=Join-Path $build 'src/ScreenCapture/LICENSE'
        'NativeCapture/LICENSE.Ling.txt'=Join-Path $build 'src/Ling/LICENSE'
        'NativeCapture/LICENSE.galaxy-tools.txt'=Join-Path $repo 'src/Pane.Capture/LICENSE.galaxy-tools.txt'
        'NativeCapture/LICENSE.Lucide.txt'=Join-Path $repo 'src/Pane.App/ThirdParty/Lucide-LICENSE.txt'
    }
    foreach($notice in 'LICENSE.cgif.txt','LICENSE.quirc.txt','LICENSE.Yoga.txt'){
        $captureAssets['NativeCapture/'+$notice]=Join-Path $repo "src/Pane.Capture/notices/$notice"
    }
}
if($WithTrafficService){
    $trafficRoot=Join-Path (Split-Path $metadata.stage -Parent) 'traffic-build'
    $builds=@(Get-ChildItem -LiteralPath $trafficRoot -Directory -Filter 'build-*')
    if($builds.Count -ne 1){throw '未能确定流量服务预览构建目录'}
    foreach($name in 'Pane.TrafficService-x64-preview.zip','Pane.TrafficService-x64-preview.zip.manifest.json'){
        $trafficAssets["TrafficService/$name"]=Join-Path $builds[0].FullName $name
    }
}
$assets=Get-Content -LiteralPath (Join-Path $repo 'src/Pane.App/obj/project.assets.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$cache=@($assets.packageFolders.PSObject.Properties.Name)[0]
# NRBF 读取器经 NativeAOT 链接，不会作为独立 DLL 出现在载荷文件归属表中。
$nrbf=@($assets.libraries.PSObject.Properties | Where-Object {$_.Name -like 'System.Formats.Nrbf/*'})
if($nrbf.Count -ne 1){throw '无法确定 NRBF 包版本'}
$nrbfFolder=Join-Path $cache $nrbf[0].Value.path
[xml]$nuspec=Get-Content -LiteralPath (Join-Path $nrbfFolder 'system.formats.nrbf.nuspec') -Encoding UTF8 -Raw
if($nuspec.package.metadata.license.InnerText -ne 'MIT'){throw 'NRBF 许可已变化，需重新核对'}
if((Get-FileHash -LiteralPath (Join-Path $nrbfFolder 'THIRD-PARTY-NOTICES.TXT')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty/dotnet-NOTICES.txt')).Hash){throw 'NRBF 上游 notices 与随包文件不一致'}
$inventory=Get-Content -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Encoding UTF8 -Raw
$nrbfVersion=$nrbf[0].Name.Split('/')[1]
if($inventory -notmatch ('(?m)^\| System\.Formats\.Nrbf,.*\| '+[regex]::Escape($nrbfVersion)+' \| MIT.*`dotnet-LICENSE\.txt`, `dotnet-NOTICES\.txt`')){throw '第三方清单缺少当前 NRBF 版本及许可'}
$candidates=@{}
foreach($library in $assets.libraries.PSObject.Properties){
    if($library.Value.type -ne 'package'){continue}
    $folder=Join-Path $cache $library.Value.path
    foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File){
        if(!$candidates.ContainsKey($file.Name)){$candidates[$file.Name]=@()}
        $candidates[$file.Name]+=,@{package=$library.Name;path=$file.FullName;length=$file.Length}
    }
}
$own=@('Pane.App.exe','Pane.Maintenance.exe','Uninstall Pane.exe','zh-Hant/Pane.App.resources.dll','Pane.Native.dll','Pane.Effect.dll','resources.pri','Assets/pane.ico','Assets/pane-app.svg','LauncherData/pinyin.tsv','LauncherData/Unicode-LICENSE.txt')
$notices=@{'LICENSE'='LICENSE';'LICENSE-EXCEPTION.md'='LICENSE-EXCEPTION.md';'THIRD_PARTY_NOTICES.md'='THIRD_PARTY_NOTICES.md'}

foreach($file in Get-ChildItem -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty') -File){$notices['ThirdParty/'+$file.Name]='src/Pane.App/ThirdParty/'+$file.Name}
$rows=@(foreach($file in Get-ChildItem -LiteralPath $metadata.stage -Recurse -File){
    $path=$file.FullName.Substring($metadata.stage.TrimEnd('\','/').Length+1).Replace('\','/')
    $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $matches=@(foreach($candidate in $candidates[$file.Name]){
        if($candidate.length -eq $file.Length -and (Get-FileHash -LiteralPath $candidate.path -Algorithm SHA256).Hash -eq $hash){$candidate.package}
    })
    $matches=@($matches | Select-Object -Unique)
    if($notices.ContainsKey($path)){
        if((Get-FileHash -LiteralPath (Join-Path $repo $notices[$path]) -Algorithm SHA256).Hash -ne $hash){throw "许可文件不一致：$path"}
        $kind='notice'
    } elseif($captureAssets.ContainsKey($path)){
        if((Get-FileHash -LiteralPath $captureAssets[$path] -Algorithm SHA256).Hash -ne $hash){throw "原生截图文件与构建来源不一致：$path"}
        $kind=if($path.EndsWith('.exe')){'native-capture'}else{'notice'}
    } elseif($trafficAssets.ContainsKey($path)){
        if((Get-FileHash -LiteralPath $trafficAssets[$path] -Algorithm SHA256).Hash -ne $hash){throw "流量服务文件与构建来源不一致：$path"}
        $kind='traffic-preview'
    } elseif($path.StartsWith('NativeOcr/',[StringComparison]::OrdinalIgnoreCase)){
        $relative=$path.Substring('NativeOcr/'.Length)
        if(!$ocrAssets.ContainsKey($relative) -or $ocrAssets[$relative] -ne $hash){throw "OCR 预览文件与锁定输入或源码构建不一致：$path"}
        $kind=if($relative.StartsWith('legal/') -or $relative -eq 'THIRD_PARTY_NOTICES.md'){'notice'}else{'ocr-preview'}
    } elseif($engineAssets -contains $path){
        if((Get-FileHash -LiteralPath (Join-Path $repo "src/Pane.App/$path") -Algorithm SHA256).Hash -ne $hash){throw "引擎图标不一致：$path"}
        $kind='search-engine-mark'
    } elseif($matches.Count){$kind='package'} elseif($own -contains $path){$kind='application-or-data'}else{throw "未归属的 payload 文件：$path"}
    [pscustomobject]@{path=$path;sha256=$hash;kind=$kind;packages=$matches}
})
foreach($path in $notices.Keys){if($rows.path -notcontains $path){throw "payload 缺少许可证：$path"}}
foreach($path in $captureAssets.Keys){if($rows.path -notcontains $path){throw "payload 缺少原生截图文件：$path"}}
foreach($path in $trafficAssets.Keys){if($rows.path -notcontains $path){throw "payload 缺少流量服务文件：$path"}}
foreach($path in $ocrAssets.Keys){if($rows.path -notcontains "NativeOcr/$path"){throw "payload 缺少 OCR 文件：$path"}}
foreach($path in $engineAssets){if($rows.path -notcontains $path){throw "payload 缺少引擎图标：$path"}}
# 核对真正传给安装外壳的 ZIP，并确认其字节完整嵌入最终安装程序。
$zipPath=Join-Path (Split-Path $metadata.stage -Parent) 'payload.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$trafficFiles=0
if($WithTrafficService){
    $trafficZip=Join-Path $metadata.stage 'TrafficService/Pane.TrafficService-x64-preview.zip'
    $manifest=Get-Content -LiteralPath "$trafficZip.manifest.json" -Encoding UTF8 -Raw | ConvertFrom-Json
    if($manifest.schema -ne 1 -or $manifest.archive -ne (Split-Path $trafficZip -Leaf) -or
        (Get-FileHash -LiteralPath $trafficZip -Algorithm SHA256).Hash -ne $manifest.sha256){throw '流量服务 ZIP 与清单不一致'}
    $trafficArchive=[IO.Compression.ZipFile]::OpenRead($trafficZip)
    $trafficSha=[Security.Cryptography.SHA256]::Create()
    $expected=@{}
    try {
        foreach($entry in $manifest.files){
            if($expected.ContainsKey($entry.path)){throw "流量服务清单包含重复文件：$($entry.path)"}
            $expected.Add($entry.path,$entry)
        }
        if(!$expected.ContainsKey('Pane.TrafficService.exe')){throw '流量服务 ZIP 缺少服务程序'}
        $trafficFiles=$expected.Count
        foreach($entry in $trafficArchive.Entries){
            $path=$entry.FullName.Replace('\','/')
            $row=$expected[$path]
            if(!$row -or $row.bytes -ne $entry.Length){throw "流量服务 ZIP 文件不一致：$path"}
            $stream=$entry.Open()
            try{$hash=[BitConverter]::ToString($trafficSha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose()}
            if($hash -ne $row.sha256){throw "流量服务 ZIP 校验失败：$path"}
            $expected.Remove($path)
        }
        if($expected.Count -ne 0){throw '流量服务 ZIP 缺少清单文件'}
    } finally {$trafficArchive.Dispose();$trafficSha.Dispose()}
}
$archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
$sha=[Security.Cryptography.SHA256]::Create()
try {
    if($archive.Entries.Count -ne $rows.Count){throw 'ZIP 文件数与 payload 不一致'}
    foreach($entry in $archive.Entries){
        $stream=$entry.Open()
        try{$hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose()}
        $row=@($rows | Where-Object {$_.path -eq $entry.FullName.Replace('\','/')})
        if($row.Count -ne 1 -or $row[0].sha256 -ne $hash){throw "ZIP 内容不一致：$($entry.FullName)"}
    }
    $zip=[IO.File]::ReadAllBytes($zipPath);$installer=[IO.File]::ReadAllBytes($metadata.installer)
    $zipHash=[BitConverter]::ToString($sha.ComputeHash($zip));$found=$false
    for($offset=0;$offset -le $installer.Length-$zip.Length;$offset++){
        if($installer[$offset] -eq 80 -and $installer[$offset+1] -eq 75 -and $installer[$offset+2] -eq 3 -and $installer[$offset+3] -eq 4){
            if([BitConverter]::ToString($sha.ComputeHash($installer,$offset,$zip.Length)) -eq $zipHash){$found=$true;break}
        }
    }
    if(!$found){throw '安装程序中未找到完整 payload ZIP'}
} finally {$archive.Dispose();$sha.Dispose()}
New-Item -ItemType Directory -Force -Path (Split-Path ([IO.Path]::GetFullPath($Report)) -Parent)|Out-Null
$audit=[ordered]@{version=$metadata.version;files=$rows.Count;embeddedZipVerified=$true;entries=$rows}
if($WithTrafficService){$audit.trafficFilesVerified=$trafficFiles}
if($WithNativeOcr){$audit.ocrFilesVerified=$ocrAssets.Count}
if($metadata.ocrSourceBuild){$audit.ocrSourceBuild=$true}
[pscustomobject]$audit | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding UTF8
Write-Host "payload=$($rows.Count); licenses=$(@($rows | Where-Object kind -EQ 'notice').Count); embeddedZipVerified=True"
Write-Host "NativeAOT component=$($nrbf[0].Name); license=MIT; sharedNoticesVerified=True"
$rows | ForEach-Object {$_.packages} | Group-Object | ForEach-Object {Write-Host "$($_.Name): $($_.Count)"}
