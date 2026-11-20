param([string]$PackageManifest,[switch]$WithTrafficService)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(!$PackageManifest){$PackageManifest=Join-Path $repo 'dist/package.json'}
$package=Get-Content -LiteralPath $PackageManifest -Raw -Encoding UTF8 | ConvertFrom-Json
$previewPayload=@{}
if($package.isolatedPreview){
    $previewPayload['Pane.App.exe']=(Get-FileHash -LiteralPath (Join-Path $package.stage 'Pane.App.exe')).Hash
    $components=@('NativeCapture')
    if($package.ocrPreview){$components+='NativeOcr'}
    if($WithTrafficService){$components+='TrafficService'}
    $stageRoot=(Resolve-Path -LiteralPath $package.stage).ProviderPath
    foreach($component in $components){
        $files=@(Get-ChildItem -LiteralPath (Join-Path $stageRoot $component) -File -Recurse)
        if(!$files.Count){throw "安装包缺少组件文件：$component"}
        foreach($file in $files){
            $relative=[IO.Path]::GetRelativePath($stageRoot,$file.FullName)
            $previewPayload[$relative]=(Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
}elseif($WithTrafficService){
    throw '流量预览验证需要隔离预览包'
}
. "$repo/scripts/release-common.ps1"
$Root=Join-Path ([IO.Path]::GetTempPath()) ('Pane-018-e2e-'+[Guid]::NewGuid().ToString('N'))
$DataDirectory=Join-Path $Root 'data';$Isolated=$true;$helper=$null
function Run-Installer([string]$Exe,[string[]]$Arguments,[string]$WorkingDirectory=$repo,[int]$ExpectedExit=0){
    $start=@{FilePath=$Exe;WorkingDirectory=$WorkingDirectory;WindowStyle='Hidden';PassThru=$true}
    if($Arguments.Count){$start.ArgumentList=$Arguments}
    $child=Start-Process @start
    $null=$child.Handle
    try {if(!$child.WaitForExit(60000)){throw '安装进程超时'};if($child.ExitCode -ne $ExpectedExit){throw "安装进程退出码 $($child.ExitCode)，预期 $ExpectedExit：$Exe"}}finally{if(!$child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
}
try {
    New-Item -ItemType Directory -Path $Root | Out-Null
    if($package.isolatedPreview){
        Run-Installer $package.installer @() -ExpectedExit 2
        Run-Installer $package.installer @('--auto-update','--self-test-root',('"{0}"' -f $Root)) -ExpectedExit 2
        Run-Installer (Join-Path $package.stage 'Pane.App.exe') @('--setup') -ExpectedExit 2
        Run-Installer (Join-Path $package.stage 'Uninstall Pane.exe') @() -ExpectedExit 2
        Run-Installer (Join-Path $package.stage 'Pane.Maintenance.exe') @('--relocate','unused','-1') -ExpectedExit 2
        if(!(Test-Path -LiteralPath (Join-Path $package.stage 'Pane.Maintenance.exe'))){throw '隔离拒绝时不应删除助手'}
        if(Get-ChildItem -LiteralPath $Root -Force){throw '隔离拒绝时不应写入测试根目录'}
        Write-Host 'PASS preview rejects non-isolated setup, direct worker, uninstall, and relay; rejects automatic update'
    }
    Run-Installer $package.installer @('--self-test-root',('"{0}"' -f $Root))
    $extracted=[IO.File]::ReadAllText((Join-Path $Root 'installed.txt')).Trim()
    if(Test-Path -LiteralPath $extracted){throw '安装外壳未清理临时文件'}
    if(!(Test-Path -LiteralPath "$Root/menu/Pane.lnk") -or !(Test-Path -LiteralPath "$Root/uninstall.json")){throw '缺少假系统集成输出'}
    Write-Host 'PASS wrapper: extracted -> setup headless -> installed -> exited -> temporary removed'
    $installRoot=[IO.Path]::GetFullPath((Join-Path $Root 'custom tools/Pane'))
    $exe=Join-Path $installRoot 'app/Pane.App.exe'
    if(!(Test-Path -LiteralPath $exe)){throw '自定义安装未完成'}
    $registration=Get-Content -LiteralPath "$Root/uninstall.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if($registration.InstallLocation -ne $installRoot){throw '登记位置不匹配'}
    Run-Installer $package.installer @('--self-test-root',('"{0}"' -f $Root))
    if(!(Test-Path -LiteralPath "$installRoot/app.old/Pane.App.exe")){throw '升级未保留旧版'}
    foreach($path in $previewPayload.Keys){
        foreach($app in 'app','app.old'){
            if((Get-FileHash -LiteralPath (Join-Path "$installRoot/$app" $path)).Hash -ne $previewPayload[$path]){throw "已安装的预览文件不一致：$app/$path"}
        }
    }
    if($previewPayload.Count){Write-Host "PASS preview payload files in app and app.old: $($previewPayload.Count) ($($components -join ', '))"}
    Write-Host 'PASS custom install -> upgrade; registered custom location; app.old preserved'
    Run-Installer $exe @('--uninstall','--self-test-root',('"{0}"' -f $Root)) (Split-Path $exe -Parent)
    $helperInfo=Get-Content -LiteralPath "$Root/helper.txt" -Encoding UTF8
    $helper=$helperInfo[0]
    $relay=Get-Process -Id ([int]$helperInfo[1]) -ErrorAction SilentlyContinue
    if($relay){try{if(!$relay.WaitForExit(60000)){throw '卸载助手超时'}}finally{$relay.Dispose()}}
    if(!(Test-Path -LiteralPath "$Root/uninstalled.txt") -or (Test-Path -LiteralPath $installRoot)){throw '卸载未完成'}
    $uninstallTemp=[IO.File]::ReadAllText("$Root/uninstalled.txt").Trim()
    if(Test-Path -LiteralPath $uninstallTemp){throw '卸载临时应用副本未删除'}
    Write-Host "PASS relocated uninstall; appFiles=0; temporaryAppFiles=0; helperRemains=$(Test-Path -LiteralPath $helper)"
    if(Get-ChildItem -LiteralPath $Root -Filter '*error.txt'){throw '自测存在错误记录'}
    if(!(Test-Path -LiteralPath (Split-Path $installRoot -Parent))){throw '父目录被删除'}
    # 031：直接给卸载中转外壳传入 app 当前目录，覆盖旧版调用者的继承行为。
    # 原中转助手已退出，先清理它；新助手仍使用既有 finally 的严格路径校验。
    if($helper){
        $resolved=[IO.Path]::GetFullPath($helper)
        if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($resolved) -notmatch '^Pane-Cleanup-[0-9a-f]{32}\.exe$'){throw '助手清理路径无效'}
        Remove-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($resolved+'.lease') -Force -ErrorAction SilentlyContinue
    }
    Run-Installer $package.installer @('--self-test-root',('"{0}"' -f $Root))
    $helper=Join-Path ([IO.Path]::GetTempPath()) ('Pane-Cleanup-'+[Guid]::NewGuid().ToString('N')+'.exe')
    Copy-Item -LiteralPath (Join-Path (Split-Path $exe -Parent) 'Pane.Maintenance.exe') -Destination $helper
    # -1 不对应进程，模拟调用者已经退出；外壳使用现有 ArgumentException 分支。
    Run-Installer $helper @('--relocate',('"{0}"' -f (Split-Path $exe -Parent)),'-1','--self-test-root',('"{0}"' -f $Root)) (Split-Path $exe -Parent)
    if(Test-Path -LiteralPath $installRoot){throw '继承 app 目录的卸载中转未删除安装'}
    if(!(Test-Path -LiteralPath "$DataDirectory/settings.json")){throw '卸载中转丢失保留的数据'}
    if(Get-ChildItem -LiteralPath $Root -Filter '*error.txt'){throw '卸载中转存在错误记录'}
    Write-Host 'PASS relocate inheritedCwd=app; installationRemoved=1; dataPreserved=1; errors=0; assertions=3'
    Write-Host 'PASS isolated setup; realRegistryWrites=0; realShortcuts=0; realClipboardReads=0'
}finally {
    foreach($file in Get-ChildItem -LiteralPath $Root -Filter '*error.txt' -ErrorAction SilentlyContinue){Get-Content -LiteralPath $file.FullName -Encoding UTF8}
    if(Test-Path -LiteralPath "$DataDirectory/logs"){Get-ChildItem -LiteralPath "$DataDirectory/logs" -Filter 'crash-*.txt' | ForEach-Object {Get-Content -LiteralPath $_.FullName -Encoding UTF8}}
    if($helper){
        $resolved=[IO.Path]::GetFullPath($helper)
        if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or [IO.Path]::GetFileName($resolved) -notmatch '^Pane-Cleanup-[0-9a-f]{32}\.exe$'){throw '助手清理路径无效'}
        Remove-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($resolved+'.lease') -Force -ErrorAction SilentlyContinue
    }
    $checkedRoot=Resolve-PaneRoot $Root
    if(!$checkedRoot.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($checkedRoot) -notlike 'Pane-018-e2e-*'){throw '清理根目录无效'}
    Remove-PaneTree $checkedRoot ([IO.Path]::GetTempPath().TrimEnd('\'))
}
