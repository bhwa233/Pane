param([ValidateSet('Both','jit','aot')][string]$Mode='Both',[switch]$Cached,[string]$OutputRoot,[switch]$IsolatedPreview)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputRoot){$OutputRoot=Join-Path $root 'bin'}
$dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
$version=& $dotnet msbuild "$root/Pane.App.csproj" -getProperty:Version
if($LASTEXITCODE -ne 0){throw '无法读取应用版本'}
$hash=& git @('-C',$root,'rev-parse','--short','HEAD')
if($LASTEXITCODE -ne 0){throw '无法读取提交哈希'}
$modes=if($Mode-eq 'Both'){@('jit','aot')}else{@($Mode)}
foreach($kind in $modes){
    $aot=if($kind-eq 'aot'){'true'}else{'false'}
    $arguments=@('publish',"$root/Pane.App.csproj",'-c','Release','-r','win-x64',"-p:PublishAot=$aot","-p:Version=$version","-p:InformationalVersion=$version+$hash","-p:IncludeSourceRevisionInInformationalVersion=false",'-o',"$OutputRoot/$kind")
    if($IsolatedPreview){$arguments+='-p:PaneIsolatedPreview=true'}
    if($Cached){$arguments+=@('--source',"$env:USERPROFILE/.nuget/packages",'-p:NuGetAudit=false')}
    & $dotnet $arguments
    if($LASTEXITCODE-ne 0){throw "$kind 构建失败"}
}
