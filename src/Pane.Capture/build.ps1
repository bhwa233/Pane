#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'bin'),
    [string]$SourceCache = (Join-Path $PSScriptRoot 'bin/cache'),
    [string]$AtlRoot = $env:PANE_CAPTURE_ATL,
    [string]$StageDirectory,
    [switch]$BuildOcr,
    [string]$OnnxRuntimeArchive,
    [string]$VcRuntimeDirectory
)
$ErrorActionPreference = 'Stop'
if ($BuildOcr -and (!$OnnxRuntimeArchive -or !$VcRuntimeDirectory -or $StageDirectory)) {
    throw 'OCR source build requires OnnxRuntimeArchive and VcRuntimeDirectory, without StageDirectory.'
}
$lockPath = Join-Path $PSScriptRoot 'sources.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$ortFiles = 'onnxruntime.dll','onnxruntime_providers_shared.dll'
$vcFiles = 'msvcp140.dll','msvcp140_1.dll','vcruntime140.dll','vcruntime140_1.dll'
if ($BuildOcr) {
    $OnnxRuntimeArchive = (Resolve-Path -LiteralPath $OnnxRuntimeArchive).Path
    $VcRuntimeDirectory = (Resolve-Path -LiteralPath $VcRuntimeDirectory).Path
    if ((Get-FileHash -LiteralPath $OnnxRuntimeArchive -Algorithm SHA256).Hash -ne $lock.ocrDependencies.onnxArchiveSha256) {
        throw 'ONNX Runtime archive does not match the locked SHA-256.'
    }
}
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'scripts/find-build-tools.ps1')
$vs = & (Find-PaneVsWhere) '-latest' '-products' '*' '-requires' 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' '-property' 'installationPath'
if (!$vs) { throw 'Microsoft Visual Studio x64 C++ Build Tools are required.' }
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
if (!$AtlRoot) { $AtlRoot = Join-Path $env:VCToolsInstallDir 'atlmfc' }
$AtlRoot = [IO.Path]::GetFullPath($AtlRoot)
if (!(Test-Path -LiteralPath (Join-Path $AtlRoot 'include/atlbase.h') -PathType Leaf)) {
    throw 'The matching x64 ATL component is required. Install it with Visual Studio, or set PANE_CAPTURE_ATL to its verified atlmfc directory.'
}
$cmakeRoot = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake'
$cmake = Join-Path $cmakeRoot 'CMake/bin/cmake.exe'
$ninja = Join-Path $cmakeRoot 'Ninja/ninja.exe'
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake.exe -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath $ninja)) { $ninja = (Get-Command ninja.exe -ErrorAction Stop).Source }

$output = [IO.Path]::GetFullPath($OutputRoot)
$cache = [IO.Path]::GetFullPath($SourceCache)
$work = Join-Path $output ('build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$sources = Join-Path $work 'src'
New-Item -ItemType Directory -Path $sources,$cache -Force | Out-Null
$sourceList = @($lock.sources)
if ($BuildOcr) { $sourceList += $lock.ocrSources }
foreach ($source in $sourceList) {
    $mirror = Join-Path $cache $source.name
    if (!(Test-Path -LiteralPath $mirror)) {
        & git.exe init --bare $mirror
        if ($LASTEXITCODE) { throw "Cannot initialize $($source.name) source cache." }
    }
    & git.exe -C $mirror cat-file -e "$($source.revision)^{commit}" 2>$null
    if ($LASTEXITCODE) {
        & git.exe -C $mirror fetch --no-tags --depth 1 $source.repository $source.revision
        if ($LASTEXITCODE) { throw "Cannot fetch pinned $($source.name) source." }
    }
    $archive = Join-Path $work "$($source.name).zip"
    & git.exe -C $mirror -c core.autocrlf=false archive --format=zip -o $archive $source.revision
    if ($LASTEXITCODE) { throw "Cannot archive pinned $($source.name) source." }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $sources $source.name)
}

& (Join-Path $PSScriptRoot 'adapt.ps1') -SourceRoot $sources -IconPath (Join-Path $repo 'src/Pane.App/Assets/pane.ico') -WithOcr:$BuildOcr
$build = Join-Path $work 'obj'
$ocrOptions = @()
if ($BuildOcr) {
    $modelRoot = Join-Path $sources 'ImageReader/ImageReader/Res'
    $models = @(Get-ChildItem -LiteralPath $modelRoot -File -Filter '*.onnx')
    if (!$lock.ocrModels -or $models.Count -ne $lock.ocrModels.Count) {
        throw 'OCR source models do not match the locked inventory.'
    }
    foreach ($model in $models) {
        if ((Get-FileHash -LiteralPath $model.FullName -Algorithm SHA256).Hash -ne $lock.ocrModels[$model.Name]) {
            throw "OCR model does not match the locked SHA-256: $($model.Name)"
        }
    }
    Expand-Archive -LiteralPath $OnnxRuntimeArchive -DestinationPath $work
    $ort = Join-Path $work $lock.ocrDependencies.onnxDirectory
    # OpenCV 为锁定 revision、参数固定，产物确定；对象目录跨打包复用，签名不符才重编。不能并行打包。
    $opencvBuild = Join-Path $PSScriptRoot 'bin/opencv-obj'
    # C++ 模块不需要 Python；跳过探测，避免要求本机安装 Python 或把解释器路径编进二进制。
    $opencvFlags = @('-DOPENCV_PYTHON_SKIP_DETECTION=ON',
        '-DBUILD_LIST=core,imgproc', '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_WITH_STATIC_CRT=ON',
        '-DBUILD_TESTS=OFF', '-DBUILD_PERF_TESTS=OFF', '-DBUILD_EXAMPLES=OFF', '-DBUILD_opencv_apps=OFF',
        '-DWITH_IPP=OFF', '-DWITH_ITT=OFF', '-DWITH_OPENCL=OFF', '-DWITH_OPENEXR=OFF', '-DWITH_JPEG=OFF',
        '-DWITH_FFMPEG=OFF', '-DWITH_MSMF=OFF', '-DWITH_DSHOW=OFF', '-DWITH_VTK=OFF', '-DWITH_LAPACK=OFF',
        '-DWITH_PNG=OFF', '-DWITH_TIFF=OFF', '-DWITH_WEBP=OFF', '-DBUILD_PROTOBUF=OFF', '-DWITH_PROTOBUF=OFF',
        '-DWITH_ADE=OFF', '-DWITH_OPENJPEG=OFF', '-DWITH_JASPER=OFF', '-DBUILD_JAVA=OFF',
        '-DBUILD_opencv_python3=OFF', '-DCPU_DISPATCH=', '-DCPU_BASELINE=SSE2')
    $opencvCacheFile = Join-Path $opencvBuild 'opencv-cache.txt'
    $opencvRevision = @($lock.ocrSources) | Where-Object { $_.name -eq 'opencv' } | ForEach-Object { $_.revision }
    $opencvSignature = "$opencvRevision`n$($opencvFlags -join ' ')`n$cmake"
    $opencvHit = (Test-Path -LiteralPath (Join-Path $opencvBuild 'OpenCVConfig.cmake')) -and (Test-Path -LiteralPath $opencvCacheFile) -and ((Get-Content -LiteralPath $opencvCacheFile -Raw) -ceq $opencvSignature)
    if ($opencvHit) {
        Write-Host "OpenCV cache hit: $opencvBuild"
    }
    else {
        if (Test-Path -LiteralPath $opencvBuild) { Remove-Item -LiteralPath $opencvBuild -Recurse -Force }
        & $cmake -S (Join-Path $sources 'opencv') -B $opencvBuild -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" '-DCMAKE_BUILD_TYPE=Release' @opencvFlags
        if ($LASTEXITCODE) { throw 'OpenCV configuration failed.' }
        & $cmake --build $opencvBuild --parallel 8
        if ($LASTEXITCODE) { throw 'OpenCV build failed.' }
        [IO.File]::WriteAllText($opencvCacheFile, $opencvSignature)
    }
    $ocrOptions = @('-DPANE_BUILD_OCR=ON', "-DOpenCV_DIR=$opencvBuild", "-DPANE_ONNXRUNTIME_ROOT=$ort")
}
& $cmake -S $PSScriptRoot -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" '-DCMAKE_BUILD_TYPE=Release' "-DPANE_CAPTURE_SOURCES=$sources" "-DPANE_CAPTURE_ATL=$AtlRoot" @ocrOptions
if ($LASTEXITCODE) { throw 'Native capture configuration failed.' }
if ($BuildOcr) {
    & $cmake --build $build --target ImageReader --parallel 4
    if ($LASTEXITCODE) { throw 'Native OCR build failed.' }
    $publish = Join-Path $work 'NativeOcr'
    New-Item -ItemType Directory -Path $publish | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'ImageReader.exe') -Destination $publish
    $runtimeHashes = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'ocr-preview.lock.psd1')
    foreach ($name in ($ortFiles + $vcFiles)) {
        $runtimeRoot = if ($name -in $ortFiles) { Join-Path $ort 'lib' } else { $VcRuntimeDirectory }
        $target = Join-Path $publish $name
        Copy-Item -LiteralPath (Join-Path $runtimeRoot $name) -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $runtimeHashes[$name]) {
            throw "Native OCR runtime does not match the locked SHA-256: $name"
        }
    }
    Copy-Item -LiteralPath $lockPath -Destination $publish
    Write-Host "Pane OCR development build: $publish"
    return
}
& $cmake --build $build --parallel 4
if ($LASTEXITCODE) { throw 'Native capture build failed.' }
$publish = if ($StageDirectory) { [IO.Path]::GetFullPath($StageDirectory) } else { Join-Path $work 'NativeCapture' }
New-Item -ItemType Directory -Path $publish | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'ScreenCapture.exe') -Destination $publish
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE.galaxy-tools.txt') -Destination $publish
Copy-Item -LiteralPath (Join-Path $sources 'ScreenCapture/LICENSE') -Destination (Join-Path $publish 'LICENSE.ScreenCapture.txt')
Copy-Item -LiteralPath (Join-Path $sources 'Ling/LICENSE') -Destination (Join-Path $publish 'LICENSE.Ling.txt')
Copy-Item -LiteralPath (Join-Path $repo 'src/Pane.App/ThirdParty/Lucide-LICENSE.txt') -Destination (Join-Path $publish 'LICENSE.Lucide.txt')
foreach ($notice in 'LICENSE.cgif.txt','LICENSE.quirc.txt','LICENSE.Yoga.txt') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "notices/$notice") -Destination $publish
}
Write-Host "Pane Capture build: $publish"
