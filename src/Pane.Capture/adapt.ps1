param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$IconPath,[switch]$WithOcr)
$ErrorActionPreference = 'Stop'
$adapted = $SourceRoot

function Edit-Exact([string]$File, [string]$Before, [string]$After) {
    $text = [IO.File]::ReadAllText($File).Replace("`r`n", "`n")
    $Before = $Before.Replace("`r`n", "`n")
    $After = $After.Replace("`r`n", "`n")
    if (-not $text.Contains($Before)) { throw "Upstream adaptation anchor changed: $File" }
    [IO.File]::WriteAllText($File, $text.Replace($Before, $After))
}

$capture = Join-Path $adapted 'ScreenCapture/Src'
Edit-Exact (Join-Path $adapted 'Ling/include/Util.h') 'constexpr auto gen = [](size_t i, size_t idx)' 'constexpr auto gen = [chars](size_t i, size_t idx)'
# rc.exe resolves resource filenames through the machine ANSI code page even
# with /c65001. Keep the embedded resource ID and JSON bytes, use an ASCII alias.
Copy-Item -LiteralPath (Join-Path $adapted 'ScreenCapture/Lang/简体中文.zh-CN.json') -Destination (Join-Path $adapted 'ScreenCapture/Lang/zh-CN.json')
Edit-Exact (Join-Path $capture 'Res/Resource.rc') '../../Lang/简体中文.zh-CN.json' '../../Lang/zh-CN.json'
Copy-Item -LiteralPath $IconPath -Destination (Join-Path $capture 'Res/logo.ico') -Force
Edit-Exact (Join-Path $capture 'Res/Resource.rc') '"FileDescription", "ScreenCapture"' '"FileDescription", "Pane Screenshot"'
Edit-Exact (Join-Path $capture 'Res/Resource.rc') '"ProductName", "ScreenCapture"' '"ProductName", "Pane"'

$header = Join-Path $PSScriptRoot 'SessionResult.h'
Copy-Item $header (Join-Path $capture 'SessionResult.h')
Edit-Exact (Join-Path $capture 'main.cpp') '#include "pch.h"' "#include `"pch.h`"`n#include `"SessionResult.h`""
Edit-Exact (Join-Path $capture 'main.cpp') '    App::init();' "    Spark233Session::trace(`"capture-main-start`" );`n    App::init();`n    Spark233Session::trace(`"capture-initialized`" );"
Edit-Exact (Join-Path $capture 'main.cpp') '    App::dispose();' "    Spark233Session::trace(`"capture-loop-exit`" );`n    App::dispose();"
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') '#include <include/Ling.h>' "#include <include/Ling.h>`n#include `"../SessionResult.h`""
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') '    App::get()->takeScreenShot(x, y, w, h, &screenImg);' "    Spark233Session::trace(`"capture-window-created`" );`n    App::get()->takeScreenShot(x, y, w, h, &screenImg);"
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') '    if (isClosed) return;' "    Spark233Session::trace(`"capture-window-closed`" );`n    if (isClosed) return;"
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') '    if (key == VK_ESCAPE) {' "    if (key == VK_ESCAPE) {`n        Spark233Session::trace(`"capture-escape`" );"
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') '    if (isRight) {' "    if (isRight) {`n        Spark233Session::trace(`"capture-right-click`" );"
Edit-Exact (Join-Path $capture 'Util.cpp') '#include "pch.h"' "#include `"pch.h`"`n#include `"SessionResult.h`""
Edit-Exact (Join-Path $capture 'Util.cpp') 'auto exePath = findImageReader();' @'
if (Spark233Session::enabled()) {
        // The host owns the OCR child; no detached native process. Publishing here
        // also records the selection under the OCR action.
        return Spark233Session::captureImage(w, h, data, L"ocr");
    }
    auto exePath = findImageReader();
'@
# Every completion path (copy, save, pin, long capture) funnels through these two
# helpers, so one injection point records the selection for all of them. The
# adapter claims the session image once; later completions reuse the record.
Edit-Exact (Join-Path $capture 'Util.cpp') 'if (w <= 0 || h <= 0 || !data) return;' @'
if (w <= 0 || h <= 0 || !data) return;
	Spark233Session::captureImage(w, h, data, L"copy");
'@
Edit-Exact (Join-Path $capture 'Util.cpp') 'return encodePng(stream.Get(), w, h, data);' @'
const auto wrote = encodePng(stream.Get(), w, h, data);
    // The OCR channel's own temporary file is not a user save: it keeps the
    // selection under its OCR action instead of claiming a second record.
    if (wrote && std::filesystem::path(path) != Spark233Session::selectionPath())
        Spark233Session::captureImage(w, h, data, L"save");
    return wrote;
'@
Edit-Exact (Join-Path $capture 'Setting.cpp') '#include "pch.h"' "#include `"pch.h`"`n#include `"SessionResult.h`""
Edit-Exact (Join-Path $capture 'Setting.cpp') 'PWSTR pathTmp;' @'
if (Spark233Session::enabled()) return Spark233Session::directory();
    PWSTR pathTmp;
'@
Edit-Exact (Join-Path $capture 'Setting.cpp') "std::filesystem::path Setting::initConfigPath()`r`n{" @'
std::filesystem::path Setting::initConfigPath()
{
    if (Spark233Session::enabled()) return Spark233Session::directory() / L"config.json";
'@

# Recording is outside the MVP. Reuse the upstream's already-vendored cgif encoder
# instead of adding its author's unrelated Rust/gifski build tree.
$gif = Join-Path $capture 'Win/VideoGif.hpp'
Edit-Exact $gif '#include "gifski.h"' '// spark233: upstream cgif implementation below is sufficient for the MVP build.'
$gifText = [IO.File]::ReadAllText($gif)
$begin = $gifText.IndexOf('    inline void createGif(GifParam* param)')
$end = $gifText.IndexOf('    inline void createGif2(GifParam* param)', $begin)
if ($begin -lt 0 -or $end -lt 0) { throw 'Upstream GIF adaptation anchor changed' }
[IO.File]::WriteAllText($gif, $gifText.Substring(0, $begin) + $gifText.Substring($end))
Edit-Exact (Join-Path $capture 'Win/CapVideo.cpp') 'VideoGif::createGif(gifParam.get());' 'VideoGif::createGif2(gifParam.get());'

# Session-scoped cooperative cancellation. The host writes a request containing
# the random session identity and the actual child PID, never a global signal.
Edit-Exact (Join-Path $capture 'main.cpp') '    Spark233Session::trace("capture-main-start" );' @'
    Spark233Session::ControlGuard control;
    Spark233Session::trace("capture-main-start" );
'@
Edit-Exact (Join-Path $capture 'main.cpp') '    Spark233Session::trace("capture-loop-exit" );' @'
    Spark233Session::trace("capture-loop-exit" );
    control.acknowledge();
'@
# OCR is optional: hide its initial toolbar entry and recheck at the action
# boundary. A disabled plugin never falls back to the upstream download page.
$tool = Join-Path $capture 'Tool/ToolCap.cpp'
Edit-Exact $tool '#include "pch.h"' "#include `"pch.h`"`n#include `"../SessionResult.h`""
Edit-Exact $tool 'for (auto& id : btnIds) {' @'
for (auto& id : btnIds) {
        if (id == L"ocr" && !ocrVisible) continue;
'@
Edit-Exact (Join-Path $capture 'Tool/ToolCap.h') 'WinCap* win;' @'
WinCap* win;
    Ling::Button* ocrButton{ nullptr };
    bool ocrVisible{ true };
    static constexpr UINT ocrTimerId{ 0x5200 };
'@
# Reuse Ling's window-owned timer and node visibility, so a running selection
# follows host availability without rebuilding the annotation UI or its state.
Edit-Exact $tool 'dpi = win->dpi;' @'
dpi = win->dpi;
    ocrVisible = Spark233Session::ocrAllowed();
    onTimer.add([this](UINT id) {
        if (id != ocrTimerId || !ocrButton) return;
        const bool available = Spark233Session::ocrAllowed();
        if (available == ocrVisible) return;
        ocrVisible = available;
        tip->hide(ocrButton);
        if (available) ocrButton->show(); else ocrButton->hide();
        refreshSize();
        this->win->layoutTool(this);
        Spark233Session::trace(available ? "capture-ocr-button-restored" : "capture-ocr-button-hidden");
    });
    onDestroy.add([this]() { killTimer(ocrTimerId); });
'@
Edit-Exact $tool 'btn->setFontSize(13.f);' @'
btn->setFontSize(16.f);
        if (btnIds[i] == L"ocr") {
            ocrButton = btn;
            if (!ocrVisible) btn->hide();
        }
'@
Edit-Exact $tool "`tshow();" @'
    if (Spark233Session::enabled()) setTimer(250, ocrTimerId);
    show();
'@
Edit-Exact (Join-Path $capture 'Win/WinCap.cpp') "void WinCap::startOcr()`r`n{" @'
void WinCap::startOcr()
{
    if (!Spark233Session::ocrAllowed()) {
        const bool english = Spark233Session::argument(L"--spark233-locale") == L"en-US";
        MessageBoxW(hwnd, english ? L"OCR is unavailable. Screenshots and annotations remain available." : L"OCR 不可用，基础截图和标注仍可使用。", L"Pane", MB_OK | MB_ICONINFORMATION);
        return;
    }
'@
# Expose the upstream pin glyph and translations in both screenshot toolbars.
# Pinning reuses WinPin and keeps the current image/history alive; it only ends
# text editing and hides the editing controls, as the existing right-click does.
$toolHeader = Join-Path $capture 'Tool/ToolCap.h'
Edit-Exact $toolHeader 'L"spliter",L"close"' 'L"spliter",L"pin",L"close"'
Edit-Exact $toolHeader 'L"",L"\ue62d"' 'L"",L"\ue6a2",L"\ue62d"'
Edit-Exact $toolHeader 'L"",L"tool.close"' 'L"",L"tool.pin",L"tool.close"'
Edit-Exact $tool 'Lang::get(btnTips[i])' 'Lang::get(btnIds[i] == L"pin" ? L"tool.pin" : btnTips[i])'
Edit-Exact $tool 'else if (btn->id == L"long")' @'
else if (btn->id == L"pin") {
        win->startPin(true);
    }
    else if (btn->id == L"long")
'@

$mainTool = Join-Path $capture 'Tool/ToolMain.cpp'
$mainHeader = Join-Path $capture 'Tool/ToolMain.h'
foreach ($header in $toolHeader, $mainHeader, (Join-Path $capture 'Tool/ToolSub.h')) {
    Edit-Exact $header 'btnSize{ 32.f }' 'btnSize{ 40.f }'
}
$subTool = Join-Path $capture 'Tool/ToolSub.cpp'
Edit-Exact $mainTool 'btn->setFontSize(13.f);' 'btn->setFontSize(16.f);'
Edit-Exact $subTool 'btn->setFontSize(13.f);' 'btn->setFontSize(16.f);'
Edit-Exact $subTool 'label->setSize(13.f, 13.f);' 'label->setSize(16.f, 16.f);'
Edit-Exact $subTool 'label->setFontSize(8.f);' 'label->setFontSize(10.f);'
Edit-Exact $mainHeader 'L"|",L"close"' 'L"|",L"pin",L"close"'
Edit-Exact $mainHeader 'L"|",L"\ue62d"' 'L"|",L"\ue6a2",L"\ue62d"'
Edit-Exact $mainTool '#include "../Lang.h"' "#include `"../Lang.h`"`n#include `"../SessionResult.h`""
Edit-Exact $mainTool 'dpi = win->dpi;' @'
dpi = win->dpi;
    if (Spark233Session::enabled() && Spark233Session::argument(L"--spark233-edit-pin") == L"true") {
        const auto pin = std::find(btnIds.begin(), btnIds.end(), L"pin");
        if (pin != btnIds.end()) {
            btnCodes.erase(btnCodes.begin() + (pin - btnIds.begin()));
            btnIds.erase(pin);
        }
    }
'@
Edit-Exact $mainTool 'tip->bind(btn, Lang::get(std::format(L"tool.{}", id)));' @'
auto label = Lang::get(std::format(L"tool.{}", id));
            if (id == L"pin" && Spark233Session::enabled() &&
                Spark233Session::argument(L"--spark233-pin-host") == L"true") label += L" (Ctrl+P)";
            tip->bind(btn, label);
'@
Edit-Exact $mainTool 'else if (btn->id == L"save")' @'
else if (btn->id == L"pin") {
        tip->hide();
        win->pin();
        return;
    }
    else if (btn->id == L"save")
'@

$capWindow = Join-Path $capture 'Win/WinCap.cpp'
$cutMask = Join-Path $capture 'Win/CutMask.cpp'
$cutMaskHeader = Join-Path $capture 'Win/CutMask.h'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'HoverSelection.h'), (Join-Path $PSScriptRoot 'HoverSelection.cpp') -Destination (Join-Path $capture 'Win')
Edit-Exact $cutMaskHeader '#include <include/Ling.h>' "#include <include/Ling.h>`n#include `"HoverSelection.h`""
Edit-Exact $cutMaskHeader @'
	// 鼠标悬停时吸附到光标下的窗口矩形，选区真的变了才返回 true
	bool highlight(POINT pos);
'@ @'
	void highlight(POINT pos);
	void highlightWheel(POINT pos, float space);
'@
Edit-Exact $cutMaskHeader "`tvoid initWinRect();`n" ''
Edit-Exact $cutMaskHeader 'std::vector<D2D1_RECT_F> winRect;' 'HoverSelection hover;'
Edit-Exact $cutMask "#include <dwmapi.h>`n" ''
Edit-Exact $cutMask 'CutMask::CutMask(Ling::WinBase* win) :win{ win }' @'
CutMask::CutMask(Ling::WinBase* win) :win{ win }, hover(
	{ win->x, win->y, static_cast<LONG>(win->x + win->w), static_cast<LONG>(win->y + win->h) },
	[this, win](RECT screen) {
		D2D1_RECT_F rect{};
		if (!IsRectEmpty(&screen)) {
			POINT origin{};
			ClientToScreen(win->hwnd, &origin);
			rect = D2D1::RectF(static_cast<float>(screen.left - origin.x), static_cast<float>(screen.top - origin.y),
				static_cast<float>(screen.right - origin.x), static_cast<float>(screen.bottom - origin.y));
		}
		if (maskRect.left == rect.left && maskRect.top == rect.top &&
			maskRect.right == rect.right && maskRect.bottom == rect.bottom) return;
		maskRect = rect;
		if (hasRect()) makeLayout();
		else layout.Reset();
		win->refresh();
	})
'@
Edit-Exact $cutMask "`tinitWinRect();`n" ''
Edit-Exact $cutMaskHeader '光标落在哪一块。选区内部是 Inside，外面被选区四边的延长线切成 8 块，各对应一个方位' '光标位于选区内部、边角附近，或选区外。'
Edit-Exact $cutMaskHeader '选区四边的延长线把窗口切成九块：中间是 Inside，外面八块各是一个方位。没框出选区时才是 None' '几何命中：只在选区内部或边角附近开始调整。'
Edit-Exact $cutMaskHeader '开始调整：记下方向和起始矩形。按的是边或角时，这一下就把那条边吸到光标处' '开始调整：记下方向和起始矩形，拖动时使用鼠标位移。'
Edit-Exact $cutMaskHeader 'POINT pressPos{};' @'
POINT pressPos{};
	bool drawing{ false };
'@
Edit-Exact $cutMask @'
bool CutMask::highlight(POINT pos)
{
	for (auto& rect : winRect)
	{
		if (pos.x > rect.left && pos.y > rect.top && pos.x < rect.right && pos.y < rect.bottom) {
			if (maskRect.left != rect.left || maskRect.top != rect.top ||
				maskRect.right != rect.right || maskRect.bottom != rect.bottom) {
				maskRect = rect;
				makeLayout();
				win->refresh();
				return true;
			}
			break;
		}
	}
	return false;
}

void CutMask::initWinRect()
{
	winRect.clear();
	EnumWindows([](HWND hwnd, LPARAM lparam)
		{
			if (!hwnd) return TRUE;
			if (!IsWindowVisible(hwnd)) return TRUE;
			BOOL cloaked = FALSE;
			DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
			if (cloaked) return TRUE;
			RECT rect;
			DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &rect, sizeof(RECT));
			if (rect.right - rect.left <= 6 || rect.bottom - rect.top <= 6) return TRUE;
			auto self = (CutMask*)lparam;
			auto win = self->win;
			if (rect.left < win->x) rect.left = win->x;
			if (rect.top < win->y) rect.top = win->y;
			if (rect.right > win->x + win->w) rect.right = (LONG)(win->x + win->w);
			if (rect.bottom > win->y + win->h) rect.bottom = (LONG)(win->y + win->h);
			auto x = (float)(rect.left - win->x);
			auto y = (float)(rect.top - win->y);
			auto r = (float)(rect.right - win->x);
			auto b = (float)(rect.bottom - win->y);
			self->winRect.push_back(D2D1::RectF(x, y, r, b));
			return TRUE;
		}, (LPARAM)this);
}
'@ @'
void CutMask::highlight(POINT pos)
{
	ClientToScreen(win->hwnd, &pos);
	hover.move(pos);
}

void CutMask::highlightWheel(POINT pos, float space)
{
	ClientToScreen(win->hwnd, &pos);
	hover.wheel(pos, space);
}
'@
Edit-Exact $cutMask @'
	auto layoutStr = std::format(L"X:{} Y:{} R:{} B:{} W:{} H:{}",
		maskRect.left, maskRect.top, maskRect.right, maskRect.bottom,
		maskRect.right - maskRect.left, maskRect.bottom - maskRect.top);
'@ @'
	auto layoutStr = std::format(L"{} × {}", static_cast<int>(maskRect.right - maskRect.left),
		static_cast<int>(maskRect.bottom - maskRect.top));
'@
Edit-Exact $cutMask @'
	// 标签被顶出窗口上边时折回 maskRect 内部
	if (layoutRect.top < 0) {
'@ @'
	POINT anchor{ win->x + static_cast<LONG>(maskRect.left), win->y + static_cast<LONG>(maskRect.top) };
	MONITORINFO monitor{ sizeof(MONITORINFO) };
	GetMonitorInfoW(MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST), &monitor);
	// 标签被顶出所在屏幕上边时折回选区内部。
	if (layoutRect.top + win->y < monitor.rcMonitor.top) {
'@
Edit-Exact $cutMask 'layout->SetMaxWidth(layoutRect.right - layoutRect.left);' @'
const float labelWidth = layoutRect.right - layoutRect.left;
	const float screenLeft = static_cast<float>(monitor.rcMonitor.left - win->x);
	const float screenRight = static_cast<float>(monitor.rcMonitor.right - win->x);
	layoutRect.left = std::clamp(layoutRect.left, screenLeft, std::max(screenLeft, screenRight - labelWidth));
	layoutRect.right = layoutRect.left + labelWidth;
	layout->SetMaxWidth(labelWidth);
'@
Edit-Exact $cutMask 'pressPos = pos;' @'
pressPos = pos;
	drawing = false;
	hover.freeze();
'@
Edit-Exact $cutMask @'
	auto [left, right] = std::minmax(pressPos.x, pos.x);
	auto [top, bottom] = std::minmax(pressPos.y, pos.y);
'@ @'
	// Use the Windows drag rectangle so small movements still select the window at mouse-down.
	if (!drawing && std::abs(pos.x - pressPos.x) <= GetSystemMetrics(SM_CXDRAG) &&
		std::abs(pos.y - pressPos.y) <= GetSystemMetrics(SM_CYDRAG)) return;
	drawing = true;
	pos.x = std::clamp(pos.x, 0L, static_cast<LONG>(win->w));
	pos.y = std::clamp(pos.y, 0L, static_cast<LONG>(win->h));
	auto [left, right] = std::minmax(pressPos.x, pos.x);
	auto [top, bottom] = std::minmax(pressPos.y, pos.y);
'@
Edit-Exact $cutMask @'
	// 选区的四条边各自延长，把整个窗口切成九块：中间那块就是选区自己，
	// 落在里面一律整体拖动；外面八块各对应一个方位，落在哪块就调哪条边/哪个角
	const int col = px < r.left ? 0 : (px > r.right ? 2 : 1);
	const int row = py < r.top ? 0 : (py > r.bottom ? 2 : 1);
'@ @'
	const float grip = 2 * strokeWidth;
	if (px < r.left - grip || px > r.right + grip || py < r.top - grip || py > r.bottom + grip)
		return MaskHit::None;
	// Keep a movable center even when the selection is narrower than two grips.
	const float gx = std::min(grip, (r.right - r.left) / 3);
	const float gy = std::min(grip, (r.bottom - r.top) / 3);
	const int col = px <= r.left + gx ? 0 : (px >= r.right - gx ? 2 : 1);
	const int row = py <= r.top + gy ? 0 : (py >= r.bottom - gy ? 2 : 1);
'@
Edit-Exact $cutMask @'
	// 抓边或抓角：按下这一下就把对应的边挪到光标位置。
	// 内部整体拖动不能这么干 —— 那会让选区瞬间跳到光标为中心的位置。
	if (adjustHit != MaskHit::None && adjustHit != MaskHit::Inside) {
		adjust(pos);
	}
'@ ''
Edit-Exact $cutMask @'
		const float cx = std::clamp(px, 0.f, win->w);
		const float cy = std::clamp(py, 0.f, win->h);
		switch (adjustHit)
		{
		case MaskHit::Left: r.left = cx; break;
		case MaskHit::Right: r.right = cx; break;
		case MaskHit::Top: r.top = cy; break;
		case MaskHit::Bottom: r.bottom = cy; break;
		case MaskHit::TopLeft: r.left = cx; r.top = cy; break;
		case MaskHit::TopRight: r.right = cx; r.top = cy; break;
		case MaskHit::BottomRight: r.right = cx; r.bottom = cy; break;
		case MaskHit::BottomLeft: r.left = cx; r.bottom = cy; break;
		default: break;
		}
		// 拖过头（比如左边越过右边）时归一化，接下来就自然变成"在拖另一条边"
		const float l = std::min(r.left, r.right), rr = std::max(r.left, r.right);
		const float t = std::min(r.top, r.bottom), b = std::max(r.top, r.bottom);
		r = D2D1::RectF(l, t, rr, b);
		// 塌到最小尺寸以下时，把动着的那条边推回来，不动锚定的那条
		const bool moveLeft = adjustHit == MaskHit::Left || adjustHit == MaskHit::TopLeft || adjustHit == MaskHit::BottomLeft;
		const bool moveTop = adjustHit == MaskHit::Top || adjustHit == MaskHit::TopLeft || adjustHit == MaskHit::TopRight;
		if (r.right - r.left < minSize) {
			if (moveLeft) r.left = r.right - minSize;
			else r.right = r.left + minSize;
		}
		if (r.bottom - r.top < minSize) {
			if (moveTop) r.top = r.bottom - minSize;
			else r.bottom = r.top + minSize;
		}
'@ @'
		const auto moveEdge = [](float edge, float anchor, float delta, float bound) {
			const float moved = std::clamp(edge + delta, 0.f, bound);
			const float minimum = std::min(minSize, std::abs(edge - anchor));
			const bool before = moved == anchor ? edge < anchor : moved < anchor;
			return before ? std::min(moved, std::max(0.f, anchor - minimum))
				: std::max(moved, std::min(bound, anchor + minimum));
		};
		const float dx = px - adjustPressPos.x, dy = py - adjustPressPos.y;
		if (adjustHit == MaskHit::Left || adjustHit == MaskHit::TopLeft || adjustHit == MaskHit::BottomLeft)
			r.left = moveEdge(r.left, r.right, dx, win->w);
		if (adjustHit == MaskHit::Right || adjustHit == MaskHit::TopRight || adjustHit == MaskHit::BottomRight)
			r.right = moveEdge(r.right, r.left, dx, win->w);
		if (adjustHit == MaskHit::Top || adjustHit == MaskHit::TopLeft || adjustHit == MaskHit::TopRight)
			r.top = moveEdge(r.top, r.bottom, dy, win->h);
		if (adjustHit == MaskHit::Bottom || adjustHit == MaskHit::BottomLeft || adjustHit == MaskHit::BottomRight)
			r.bottom = moveEdge(r.bottom, r.top, dy, win->h);
		if (r.left > r.right) std::swap(r.left, r.right);
		if (r.top > r.bottom) std::swap(r.top, r.bottom);
'@
Edit-Exact $capWindow '    onMouseMove.add([this](POINT pos) { this->onMove(pos); });' @'
    onMouseMove.add([this](POINT pos) { this->onMove(pos); });
    onMouseWheel.add([this](POINT pos, float space) {
        if (stage == CapStage::Select && !isPress) cutMask->highlightWheel(pos, space);
    });
'@
Edit-Exact $capWindow @'
    ScreenToClient(hwnd, &pos);
    getPixImg(pos);
    setPixPos(pos);
    show();
'@ @'
    ScreenToClient(hwnd, &pos);
    getPixImg(pos);
    setPixPos(pos);
    cutMask->highlight(pos);
    show();
'@
Edit-Exact $capWindow @'
        cutMask->startMakeRect(pos);
    }
    else if (stage == CapStage::Adjust) {
        // 选区外面按下不是重新框选，而是按落点所在的那一块调对应的边或角
        isPress = true;
        cutMask->startAdjust(pos);
        layoutTool(toolCap.get());
    }
'@ @'
        cutMask->startMakeRect(pos);
        SetCapture(hwnd);
    }
    else if (stage == CapStage::Adjust && cutMask->hitTest(pos) != MaskHit::None) {
        isPress = true;
        cutMask->startAdjust(pos);
        SetCapture(hwnd);
    }
'@
Edit-Exact $capWindow @'
void WinCap::onUp(POINT pos, bool isRight)
{
    if (stage == CapStage::Select) {
        isPress = false;
'@ @'
void WinCap::onUp(POINT pos, bool isRight)
{
    if (stage == CapStage::Select) {
        if (isRight || !isPress) return;
        isPress = false;
        ReleaseCapture();
        cutMask->makeRect(pos);
        lastDownTime = 0;
'@
Edit-Exact $capWindow @'
    else if (stage == CapStage::Adjust) {
        isPress = false;
    }
    else if (stage == CapStage::Long && capLong) {
        capLong->onUp(pos);
'@ @'
    else if (stage == CapStage::Adjust && isPress && !isRight) {
        isPress = false;
        ReleaseCapture();
        cutMask->adjust(pos);
        layoutTool(toolCap.get());
    }
    else if (stage == CapStage::Long && capLong) {
        capLong->onUp(pos);
'@
Edit-Exact (Join-Path $capture 'Win/WinCap.h') 'void startPin();' 'void startPin(bool pinned = false);'
Edit-Exact $capWindow 'void WinCap::startPin()' 'void WinCap::startPin(bool pinned)'
Edit-Exact $capWindow 'int(maskRect.right - maskRect.left), int(maskRect.bottom - maskRect.top));' 'int(maskRect.right - maskRect.left), int(maskRect.bottom - maskRect.top), pinned);'

$pinWindow = Join-Path $capture 'Win/WinPin.cpp'
$pinHeader = Join-Path $capture 'Win/WinPin.h'
$longWindow = Join-Path $capture 'Win/CapLong.cpp'
$longHeader = Join-Path $capture 'Win/CapLong.h'
Edit-Exact $longWindow '#include "CapLong.h"' "#include `"CapLong.h`"`n#include `"../SessionResult.h`""
Edit-Exact $longHeader 'int resultH{ 0 };' "int resultH{ 0 };`n        int maxResultHeight{ 36000 };`n        bool sizeLimitReached{ false };"
Edit-Exact $longWindow '    resultH = imgH;' @'
    if (imgW <= 0 || imgH <= 0) { stopCap(); return; }
    if (Spark233Session::enabled()) {
        const auto sideArgument = Spark233Session::argument(L"--spark233-capture-max-side");
        const auto pixelArgument = Spark233Session::argument(L"--spark233-capture-max-pixels");
        const auto hostSide = sideArgument.empty() ? 16000 : std::stoi(sideArgument);
        const auto hostPixels = pixelArgument.empty() ? 64000000 : std::stoi(pixelArgument);
        const auto bitmapSide = static_cast<int>(Ling::D2D::get()->deviceContext->GetMaximumBitmapSize());
        const auto maxSide = (std::min)(hostSide, bitmapSide);
        maxResultHeight = (std::min)(maxSide, hostPixels / imgW);
        if (imgW > maxSide || imgH > maxResultHeight) {
            sizeLimitReached = true;
            stopCap();
            return;
        }
    }
    resultH = imgH;
'@
Edit-Exact $longWindow '    int newResultH = paintStart + (imgH - changeStartY);' @'
    int newResultH = paintStart + (imgH - changeStartY);
    if (newResultH > maxResultHeight) {
        sizeLimitReached = true;
        stopCap();
        return;
    }
'@
Edit-Exact $longWindow '    if (resultH > 36000) { stopCap(); return; }' ''
Edit-Exact $longWindow '    if (resultH > 36000) {' '    if (sizeLimitReached) {'
Edit-Exact $longWindow '    WinPin::initFromData(posX, posY, imgW, resultH, imgData);' @'
    const bool pinToHost = Spark233Session::enabled() && Spark233Session::argument(L"--spark233-pin-host") == L"true";
    WinPin::initFromData(posX, posY, imgW, resultH, imgData, pinToHost);
'@
Edit-Exact $pinHeader 'static void initFromData(int x, int y, int w, int h, std::vector<BYTE>& data);' 'static void initFromData(int x, int y, int w, int h, std::vector<BYTE>& data, bool pinned = false);'
Edit-Exact $pinHeader 'static void init(int x, int y, int w, int h);' 'static void init(int x, int y, int w, int h, bool pinned = false);'
Edit-Exact $pinHeader 'void layoutTools();' "void layoutTools();`n    void pin();"
Edit-Exact $pinWindow 'void WinPin::init(int x, int y, int w, int h)' 'void WinPin::init(int x, int y, int w, int h, bool pinned)'
Edit-Exact $pinWindow 'ptr->createNativeWindow(WS_EX_TOPMOST| WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);' @'
ptr->createNativeWindow(WS_EX_TOPMOST| WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);
    if (pinned) ptr->pin();
'@
Edit-Exact $pinWindow 'void WinPin::onDown(POINT pos, BOOL isRight)' @'
void WinPin::pin()
{
    if (Spark233Session::enabled() && !hostPinFallback && Spark233Session::argument(L"--spark233-pin-host") == L"true") {
        if (hostPinPending) return;
        try {
            if (editingText) editingText->finishEdit();
            std::vector<BYTE> composed, original;
            D2D1_SIZE_U size{}, originalSize{};
            if (!getImagePixels(composed, size) || !getImagePixels(original, originalSize, false))
                throw std::runtime_error("Cannot read the editable pin image");
            if (size.width != originalSize.width || size.height != originalSize.height)
                throw std::runtime_error("Pin source image dimensions changed");
            const auto directory = Spark233Session::directory();
            const auto writeImage = [&](const wchar_t* filename, const std::vector<BYTE>& pixels) {
                const auto target = directory / filename;
                const auto partial = std::filesystem::path(target.wstring() + L".partial");
                Spark233Session::writePng(partial, size.width, size.height, pixels.data());
                if (!MoveFileExW(partial.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
                    throw std::runtime_error("Cannot commit the pin image");
            };
            const auto document = Spark233PinDocument::save(*this);
            writeImage(L"pin.png", composed);
            writeImage(L"pin-source.png", original);
            Spark233PinDocument::publish(directory / L"pin-document.json", document);
            if (hostPinAttempt >= 1000000) throw std::runtime_error("Pin attempt limit reached");
            ++hostPinAttempt;
            auto result = Spark233Session::envelope(L"pin");
            result.SetNamedValue(L"action", winrt::Windows::Data::Json::JsonValue::CreateStringValue(L"pin"));
            result.SetNamedValue(L"image", winrt::Windows::Data::Json::JsonValue::CreateStringValue(L"pin.png"));
            result.SetNamedValue(L"source", winrt::Windows::Data::Json::JsonValue::CreateStringValue(L"pin-source.png"));
            result.SetNamedValue(L"document", winrt::Windows::Data::Json::JsonValue::CreateStringValue(L"pin-document.json"));
            Spark233PinDocument::number(result, L"attempt", hostPinAttempt);
            Spark233PinDocument::number(result, L"width", size.width);
            Spark233PinDocument::number(result, L"height", size.height);
            Spark233PinDocument::number(result, L"scale", scale);
            RECT actual{};
            if (!GetWindowRect(hwnd, &actual)) throw std::runtime_error("Cannot read pin window bounds");
            Spark233PinDocument::number(result, L"x", actual.left);
            Spark233PinDocument::number(result, L"y", actual.top);
            winrt::Windows::Data::Json::JsonObject bounds;
            Spark233PinDocument::number(bounds, L"x", actual.left);
            Spark233PinDocument::number(bounds, L"y", actual.top);
            Spark233PinDocument::number(bounds, L"width", actual.right - actual.left);
            Spark233PinDocument::number(bounds, L"height", actual.bottom - actual.top);
            result.SetNamedValue(L"bounds", bounds);
            const auto ownerText = Spark233Session::argument(L"--spark233-pin-owner-pid");
            if (ownerText.empty()) throw std::runtime_error("Missing pin host identity");
            if (!ownerText.empty()) {
                if (ownerText.find_first_not_of(L"0123456789") != std::wstring::npos)
                    throw std::runtime_error("Invalid pin host identity");
                const auto owner = wcstoul(ownerText.c_str(), nullptr, 10);
                if (!owner || owner == GetCurrentProcessId()) throw std::runtime_error("Invalid pin host identity");
                hostPinOwner = OpenProcess(SYNCHRONIZE, FALSE, owner);
                if (!hostPinOwner) {
                    hostPinFallback = true;
                    throw std::runtime_error("Pin host no longer exists");
                }
            }
            Spark233PinDocument::publish(directory / L"pin.json", result);
            hostPinPending = true;
            shapeHover = nullptr;
            toolMain->cancelSelect();
            toolMain->hide();
            toolSub->hideTools();
            setTimer(100, hostPinTimer);
            Spark233Session::trace("pin-awaiting-host");
            refresh();
        } catch (...) {
            if (hostPinOwner) { CloseHandle(hostPinOwner); hostPinOwner = nullptr; }
            Spark233Session::trace("pin-publish-failed");
            MessageBoxW(hwnd, L"Cannot hand this editable pin to the desktop. The editor and annotations have been retained.", L"Pane", MB_OK | MB_ICONERROR);
        }
        return;
    }
    if (editingText) editingText->finishEdit();
    shapeHover = nullptr;
    toolMain->cancelSelect();
    toolMain->hide();
    toolSub->hideTools();
    refresh();
}

void WinPin::onDown(POINT pos, BOOL isRight)
'@

# Persist the original bitmap plus the existing editable History objects before
# handing a read-only pin to the host. No upstream shape is flattened or replaced.
Copy-Item (Join-Path $PSScriptRoot 'PinDocument.h') (Join-Path $capture 'PinDocument.h')
foreach ($shapeName in @('ShapeRect', 'ShapeEllipse', 'ShapeArrow', 'ShapeLine', 'ShapeNumber', 'ShapeText', 'ShapeMosaic', 'ShapeEraser')) {
    $shapeHeader = Join-Path $capture "Shape/$shapeName.h"
    $shapeAnchor = "class $shapeName : public ShapeBase`r`n{"
    Edit-Exact $shapeHeader $shapeAnchor ($shapeAnchor + "`r`n    friend struct Spark233PinDocument;")
}
Edit-Exact $pinWindow '#include "WinPin.h"' "#include `"WinPin.h`"`n#include `"../SessionResult.h`"`n#include `"../PinDocument.h`""
Edit-Exact $pinWindow "void WinPin::onCreated()`r`n{" @'
void WinPin::onCreated()
{
    if (Spark233Session::enabled()) {
        const bool english = Spark233Session::argument(L"--spark233-locale") == L"en-US";
        SetWindowTextW(hwnd, english ? L"Pane Screenshot annotations" : L"Pane 截图标注");
        SetWindowTextW(toolMain->hwnd, english ? L"Pane Screenshot toolbar" : L"Pane 截图工具栏");
        if (Spark233Session::argument(L"--spark233-pin-host") == L"true") {
            // The host closes this temporary editing work surface after pin
            // handoff. Keep it reachable through Alt+Tab while annotations are
            // active; permanent pins remain in the host's taskbar-free layer.
            const LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (style & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);
            SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
'@
Edit-Exact $pinHeader 'void pin();' @'
void pin();
    static void editPinnedDocument();
'@
Edit-Exact $pinHeader 'bool dpiChanged{ false };' @'
bool dpiChanged{ false };
    bool hostPinPending{ false };
    bool hostPinFallback{ false };
    HANDLE hostPinOwner{ nullptr };
    unsigned hostPinAttempt{ 0 };
    static constexpr UINT hostPinTimer{ 0x53A1 };
'@
Edit-Exact $pinWindow "WinPin::~WinPin()`r`n{" "WinPin::~WinPin()`r`n{`r`n    if (hostPinOwner) CloseHandle(hostPinOwner);"
Edit-Exact $pinHeader 'bool getImagePixels(std::vector<BYTE>& pixels, D2D1_SIZE_U& size);' 'bool getImagePixels(std::vector<BYTE>& pixels, D2D1_SIZE_U& size, bool annotations = true);'
Edit-Exact $pinWindow 'bool WinPin::getImagePixels(std::vector<BYTE>& pixels, D2D1_SIZE_U& size)' 'bool WinPin::getImagePixels(std::vector<BYTE>& pixels, D2D1_SIZE_U& size, bool annotations)'
$pinText = [IO.File]::ReadAllText($pinWindow)
$pixelsStart = $pinText.IndexOf('bool WinPin::getImagePixels')
if ($pixelsStart -lt 0 -or -not $pinText.Substring($pixelsStart).Contains('if (!shape->isUndo)')) { throw 'Upstream pin pixel export anchor changed' }
[IO.File]::WriteAllText($pinWindow, $pinText.Substring(0, $pixelsStart) + $pinText.Substring($pixelsStart).Replace('if (!shape->isUndo)', 'if (annotations && !shape->isUndo)'))

foreach ($signature in @('void WinPin::onDown(POINT pos, BOOL isRight)', 'void WinPin::onMove(POINT pos)', 'void WinPin::onUp(POINT pos, BOOL isRight)', 'void WinPin::onKey(UINT key)')) {
    Edit-Exact $pinWindow ($signature + "`r`n{") ($signature + "`r`n{`r`n    if (hostPinPending) return;")
}
Edit-Exact $pinWindow "if (ctrl && key == 'Z')" @'
if (ctrl && key == 'P' && Spark233Session::enabled() &&
        Spark233Session::argument(L"--spark233-pin-host") == L"true" &&
        Spark233Session::argument(L"--spark233-edit-pin") != L"true") {
        pin();
    }
    else if (ctrl && key == 'Z')
'@
Edit-Exact $pinWindow "void WinPin::saveToFile()`r`n{" @'
void WinPin::saveToFile()
{
    if (Spark233Session::enabled() && Spark233Session::argument(L"--spark233-edit-pin") == L"true") {
        pin();
        return;
    }
'@
Edit-Exact $pinWindow 'onMouseWheel.add([this](POINT pos, float space) {' @'
onMouseWheel.add([this](POINT pos, float space) {
        if (hostPinPending) return;
'@
Edit-Exact $pinWindow "void WinPin::onTimerCB(UINT id)`r`n{" @'
void WinPin::onTimerCB(UINT id)
{
    if (id == hostPinTimer && hostPinPending) {
        const auto session = winrt::to_string(Spark233Session::argument(L"--spark233-session"));
        const auto identity = session + ":" + std::to_string(GetCurrentProcessId()) + ":" + std::to_string(hostPinAttempt) + ":";
        const auto acknowledgementPath = Spark233Session::directory() / (L"pin-" + std::to_wstring(hostPinAttempt) + L".ack");
        std::ifstream file(acknowledgementPath, std::ios::binary);
        std::string acknowledgement;
        if (file) std::getline(file, acknowledgement);
        if (!acknowledgement.empty() && acknowledgement.back() == '\r') acknowledgement.pop_back();
        const bool ownerExited = hostPinOwner && WaitForSingleObject(hostPinOwner, 0) == WAIT_OBJECT_0;
        if (acknowledgement == identity + "accepted") {
            hostPinPending = false;
            killTimer(hostPinTimer);
            if (hostPinOwner) { CloseHandle(hostPinOwner); hostPinOwner = nullptr; }
            Spark233Session::trace("pin-host-accepted");
            close();
        } else if (acknowledgement == identity + "rejected" || ownerExited) {
            hostPinPending = false;
            killTimer(hostPinTimer);
            if (hostPinOwner) { CloseHandle(hostPinOwner); hostPinOwner = nullptr; }
            if (ownerExited) hostPinFallback = true;
            layoutTools();
            toolMain->show();
            refresh();
            Spark233Session::trace(ownerExited ? "pin-owner-exited-editor-restored" : "pin-host-rejected-editor-restored");
        }
        return;
    }
'@

Edit-Exact $pinWindow "void WinPin::initFromData(int x, int y, int w, int h, std::vector<BYTE>& data)`n{`n`tauto ptr = new WinPin(x, y, w, h, &data);`n`tstd::unique_ptr<WinPin> winPin{ ptr };`n`tptr->createNativeWindow(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);" @'
void WinPin::editPinnedDocument()
{
    const auto directory = Spark233Session::directory();
    UINT width = 0, height = 0;
    auto pixels = Spark233PinDocument::decode(directory / L"edit-source.png", width, height);
    const auto document = Spark233PinDocument::read(directory / L"edit-document.json");
    const auto boundsFile = directory / L"edit-bounds.json";
    int left = 100, top = 100;
    float viewScale = Spark233PinDocument::number(document, L"scale");
    if (std::filesystem::exists(boundsFile)) {
        const auto bounds = Spark233PinDocument::read(boundsFile);
        left = static_cast<int>(Spark233PinDocument::number(bounds, L"x"));
        top = static_cast<int>(Spark233PinDocument::number(bounds, L"y"));
        viewScale = std::clamp(std::min(Spark233PinDocument::positive(bounds, L"width") / width,
            Spark233PinDocument::positive(bounds, L"height") / height), 0.1f, 8.f);
    }
    auto ptr = new WinPin(left, top, width, height, &pixels);
    std::unique_ptr<WinPin> owned{ptr};
    ptr->createNativeWindow(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);
    Spark233PinDocument::restore(*ptr, document);
    const auto maxScale = std::max(1.f, std::min(8.f, 16000.f / std::max(width, height)));
    ptr->scale = std::clamp(viewScale, 0.1f, maxScale);
    ptr->applyWinSize();
    ptr->setPosition(left, top);
    ptr->layoutTools();
    ptr->refresh();
    winPins.push_back(std::move(owned));
    Spark233Session::trace("pin-document-opened");
}

void WinPin::initFromData(int x, int y, int w, int h, std::vector<BYTE>& data, bool pinned)
{
    auto ptr = new WinPin(x, y, w, h, &data);
    std::unique_ptr<WinPin> winPin{ ptr };
    ptr->createNativeWindow(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_POPUP);
    if (pinned) ptr->pin();
'@

Edit-Exact (Join-Path $capture 'App.cpp') '#include "App.h"' "#include `"App.h`"`n#include `"SessionResult.h`""
Edit-Exact (Join-Path $capture 'App.cpp') '    app.reset(ptr);' @'
    app.reset(ptr);
    // WinPin consults App::get() while creating its native windows.
    // Restore only after the application singleton has been assigned.
    if (Spark233Session::enabled() && Ling::App::get()->args[L"--spark233-edit-pin"] == L"true")
        WinPin::editPinnedDocument();
'@
Edit-Exact (Join-Path $capture 'App.cpp') '    if (app->args[L"--auto-quit"] == L"true") {' @'
    if (Spark233Session::enabled() && app->args[L"--spark233-edit-pin"] == L"true") {
        return;
    }
    if (app->args[L"--auto-quit"] == L"true") {
'@
Edit-Exact (Join-Path $capture 'main.cpp') '    App::init();' @'
    try { App::init(); }
    catch (...) {
        // Do not enter a fresh capture when an editable document is corrupt.
        // Returning before the message loop also avoids dispatch to partial UI.
        Spark233Session::trace(Spark233Session::argument(L"--spark233-edit-pin") == L"true" ?
            "pin-document-open-failed" : "capture-initialization-failed");
        return 1;
    }
'@

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets/pane-capture-icons.ttf') -Destination (Join-Path $capture 'Res/pane-capture-icons.ttf')
Edit-Exact (Join-Path $capture 'Res/Resource.rc') 'icon.ttf RCDATA "iconfont.ttf"' 'icon.ttf RCDATA "pane-capture-icons.ttf"'
$icons = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'icons.json') -Raw | ConvertFrom-Json -AsHashtable
Edit-Exact (Join-Path $capture 'Tool/ToolSub.cpp') 'label->setText(L"\ue6ad")' ('label->setText(L"\u' + $icons.colorCheck.codepoint + '")')
Edit-Exact (Join-Path $capture 'Tool/ToolVideo.cpp') 'auto btnStart = makeIconBtn(L"\ue660")' ('auto btnStart = makeIconBtn(L"\u' + $icons.recordStart.codepoint + '")')
$toolSources = Get-ChildItem -LiteralPath (Join-Path $capture 'Tool') -File -Include '*.cpp','*.h'
$iconSources = @($toolSources.FullName) + @('Win/WinSetting.cpp','Win/WinSettingCommon.cpp' | ForEach-Object { Join-Path $capture $_ })
$specialCodepoints = @($icons.colorCheck.codepoint, $icons.recordStart.codepoint)
foreach ($source in $iconSources) {
    $text = [IO.File]::ReadAllText($source)
    $updated = [regex]::Replace($text, '\\u([ed][0-9a-f]{3})', {
        param($match)
        $codepoint = $match.Groups[1].Value
        if ($icons.Contains($codepoint)) { return '\u' + $icons[$codepoint].codepoint }
        if ($specialCodepoints -contains $codepoint) { return $match.Value }
        throw "Unmapped screenshot icon U+$codepoint in $source"
    }).Replace('setFontFamily(L"icon")', 'setFontFamily(L"lucide")')
    if ($updated -cne $text) { [IO.File]::WriteAllText($source, $updated) }
}

if ($WithOcr) {
    $reader = Join-Path $adapted 'ImageReader/ImageReader'
    Copy-Item -LiteralPath $header -Destination $reader
    Copy-Item -LiteralPath $IconPath -Destination (Join-Path $reader 'Res/logo.ico') -Force
    Edit-Exact (Join-Path $reader 'Res/Resource.rc') '"FileDescription", "ImageReader"' '"FileDescription", "Pane OCR"'
    Edit-Exact (Join-Path $reader 'Res/Resource.rc') '"ProductName", "ImageReader"' '"ProductName", "Pane"'
    foreach ($name in 'ImgViewer.cpp','WindowMain.cpp','main.cpp') {
        Edit-Exact (Join-Path $reader $name) '#include "pch.h"' "#include `"pch.h`"`n#include `"SessionResult.h`""
    }
    Edit-Exact (Join-Path $reader 'ImgViewer.cpp') 'doc.loadResult(result);' @'
if (Spark233Session::enabled()) Spark233Session::recognition(result, w, h);
	else doc.loadResult(result);
'@
    Edit-Exact (Join-Path $reader 'WindowMain.cpp') '    ins->loadImgFromArgs();' @'
    ins->loadImgFromArgs();
    if (Spark233Session::enabled() && Spark233Session::argument(L"--spark233-batch") == L"true")
        Ling::App::get()->quit();
'@
    Edit-Exact (Join-Path $reader 'WindowMain.cpp') '    show();' @'
    if (!Spark233Session::enabled() || Spark233Session::argument(L"--spark233-batch") != L"true") show();
'@
    Edit-Exact (Join-Path $reader 'main.cpp') '    WindowMain::init();' @'
    Spark233Session::ControlGuard control;
    WindowMain::init();
'@
    Edit-Exact (Join-Path $reader 'main.cpp') '    WindowMain::dispose();' @'
    control.acknowledge();
    WindowMain::dispose();
'@
}
