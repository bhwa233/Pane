# Pane Capture

Pane starts this Windows x64 tool on demand, using a new session directory for
each selection. It is not a service and does not use an installed Galaxy copy.
Pane owns screenshot history and desktop layout. The native tool owns region
selection and annotations. When the editor pins a capture, Pane stores the
rendered image, original image and annotation document in one history entry,
then adds its desktop card before acknowledging the pin. A rejection leaves
the native editor active. The pencil button in screenshot history opens the
stored original and annotations. Save or Ctrl+S updates the same history
entry and its existing card views without changing the layout. Close without
saving leaves the previous revision intact. With the optional local OCR runtime
present, a new capture can send its selection to Pane's screenshot history for
recognition. The native tool does not launch the OCR engine, and the OCR action
is not enabled when reopening an edit.

## Development build

From the repository root, run `pwsh -File src/Pane.Capture/build.ps1`. The build
needs Git, Visual Studio x64 C++ Build Tools, the matching ATL component, CMake
3.24 or later, and Ninja. `PANE_CAPTURE_ATL` can point to an existing matching
`atlmfc` directory. The script does not install build tools. Its default source
cache is under `bin/cache`; an optional `-SourceCache` can point to Git caches
with the commits in `sources.lock.json`. Dirty checkout files are not used.

The build reports a `NativeCapture` directory with the executable and notices.
For an isolated app preview, place that directory next to `Pane.App.exe`.
`scripts/package.ps1 -WithNativeCapture` builds the same directory into an
opt-in installer payload under `out/capture-preview`. Standard packages do not
include the component until the Windows capture and installation checks pass.
Run `pwsh -File tests/export/audit-payload.ps1 -WithNativeCapture` to verify the
preview's executable and notices against their build sources, and every ZIP
entry against the staged payload. This verifies the package without running it.

For local OCR validation, add `-IsolatedPreview -OcrPreviewSource <ocr-assets>`
to the native capture package command. The input is an explicitly prepared OCR
asset tree laid out like the existing `packages/official/ocr` directory: seven
x64 binaries and `resources.json` in `native/win32-x64`, plus the listed notices.
No Galaxy installation is needed to run the preview. Packaging accepts only the
hashes in `ocr-preview.lock.psd1`; the packaging command neither downloads nor
builds this runtime.
Audit the resulting installer using `-WithNativeCapture -WithNativeOcr` with
the run's explicit `-PackageMetadata` and a separate `-Report` path. This local
preview is not approved for distribution. Pane-specific authorization for the
engine and models, release integration, and final notices remain separate gates.

Add `-OcrOnnxRuntimeArchive <onnxruntime-win-x64-1.23.2.zip>` to build the OCR
engine from source during packaging. In this mode `-OcrPreviewSource` supplies
only the four VC runtime DLLs and the locked notices. The package does not read
its `ImageReader.exe`, ONNX DLLs or `resources.json`. The source-built payload
includes `sources.lock.json` instead. The same audit command verifies the
engine against the compiler output from the package's own build directory, and
still verifies all runtime DLLs and notices against the pinned hashes.

### OCR source build

```powershell
pwsh -File src/Pane.Capture/build.ps1 -BuildOcr `
  -OnnxRuntimeArchive <onnxruntime-win-x64-1.23.2.zip> `
  -VcRuntimeDirectory <directory-containing-the-four-x64-VC-runtime-DLLs>
```

Supply the original ONNX Runtime archive and the four app-local VC runtime DLLs
from the locked preview inputs. Pane verifies the ONNX archive and all six
runtime DLL hashes. The builder compiles OpenCV `core,imgproc` with the same
x64 MSVC toolchain and static CRT as ImageReader; it does not reuse an OpenCV
binary compiled with a different toolset.

The builder fetches the pinned OpenCV, ImageReader and TinyOCR sources, applies the Pane
batch protocol and builds `ImageReader.exe` with the two models in ImageReader's
`ImageReader/Res` directory. The source build verifies the ONNX filenames and SHA-256
hashes in the `ocrModels` inventory before compiling, and includes the lock in its output.
Source and model fingerprints identify these exact build inputs; they do not document
their original upstream weight revision or grant permission to redistribute them. The output
`NativeOcr` directory is for local development: place it beside an isolated
`Pane.App.exe` to try the engine. The development build is not an installer and
does not add files to a regular Pane package.

## Source provenance

- ScreenCapture 2.5.15 and Ling: the exact upstream revisions are in
  `sources.lock.json`; the build copies their unmodified license files.
- The session and editable-pin adapters originate from Galaxy Tools commit
  `fd8f11eaa55322f5d2d1c02d234d48596afe41f2` (see `LICENSE.galaxy-tools.txt`).
  Pane maintains its own copy and builds against the pinned upstream sources.
- Yoga comes from the pinned Ling tree. cgif and quirc come from the pinned
  ScreenCapture tree. Their upstream license texts are staged as
  `LICENSE.Yoga.txt`, `LICENSE.cgif.txt` and `LICENSE.quirc.txt`.
- The screenshot toolbar uses a subset of Pane's Lucide `1.60.0` icon font;
  the original upstream font is not embedded. See [icon provenance and
  regeneration](assets/README.md). The executable uses the Pane application icon.
