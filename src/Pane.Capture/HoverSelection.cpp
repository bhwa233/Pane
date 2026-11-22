#include "HoverSelection.h"
#include <include/Ling.h>
#include <UIAutomation.h>
#include <dwmapi.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <optional>
#include <string_view>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

struct HoverSelection::State
{
    struct Window { HWND hwnd; RECT bounds; };
    struct Query {
        Window window;
        POINT point;
        uint64_t sequence;
        std::optional<RECT> descendFrom;
    };

    std::function<void(RECT)> show;
    winrt::Windows::System::DispatcherQueue dispatcher;
    std::vector<Window> windows;
    std::vector<RECT> path;
    size_t level{};
    const Window* hovered{};
    POINT point{};
    bool locked{};

    std::mutex gate;
    std::condition_variable wake;
    std::optional<Query> pending;
    std::atomic_uint64_t sequence{};
    bool stopping{};

    State(RECT desktop, std::function<void(RECT)> show)
        : show(std::move(show)), dispatcher(Ling::App::get()->dq)
    {
        struct Snapshot { RECT desktop; std::vector<Window>& windows; } snapshot{desktop, windows};
        EnumWindows([](HWND hwnd, LPARAM context) -> BOOL {
            auto& snapshot = *reinterpret_cast<Snapshot*>(context);
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || hwnd == GetShellWindow()) return TRUE;
            wchar_t className[256]{};
            GetClassNameW(hwnd, className, ARRAYSIZE(className));
            if (std::wstring_view(className) == L"WorkerW") return TRUE;
            BOOL cloaked{};
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
            RECT frame{}, bounds{};
            if (!cloaked && SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame))) &&
                IntersectRect(&bounds, &frame, &snapshot.desktop))
                snapshot.windows.push_back({hwnd, bounds});
            return TRUE;
        }, reinterpret_cast<LPARAM>(&snapshot));
    }

    void request(POINT screen, std::optional<RECT> descendFrom = {})
    {
        {
            std::scoped_lock lock(gate);
            pending = Query{*hovered, screen, ++sequence, descendFrom};
        }
        wake.notify_one();
    }

    void cancel()
    {
        std::scoped_lock lock(gate);
        ++sequence;
        pending.reset();
    }

    static void run(std::shared_ptr<State> self);
};

void HoverSelection::State::run(std::shared_ptr<State> self)
{
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return;
    {
        ComPtr<IUIAutomation> client;
        ComPtr<IUIAutomationCondition> condition;
        ComPtr<IUIAutomationCacheRequest> cache;
        if (SUCCEEDED(CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&client))) &&
            SUCCEEDED(client->get_ControlViewCondition(&condition)) && SUCCEEDED(client->CreateCacheRequest(&cache)) &&
            SUCCEEDED(cache->put_TreeFilter(condition.Get())) && SUCCEEDED(cache->put_TreeScope(TreeScope_Children)) &&
            SUCCEEDED(cache->AddProperty(UIA_BoundingRectanglePropertyId)) && SUCCEEDED(cache->AddProperty(UIA_IsOffscreenPropertyId))) {
            for (;;) {
                Query query;
                {
                    std::unique_lock lock(self->gate);
                    self->wake.wait(lock, [&] { return self->stopping || self->pending.has_value(); });
                    if (self->stopping) break;
                    query = *self->pending;
                    self->pending.reset();
                }

                // Point hits on the desktop would find our own overlay. Start at the captured app window instead.
                ComPtr<IUIAutomationElement> parent;
                client->ElementFromHandle(query.window.hwnd, &parent);
                std::vector<RECT> path{query.window.bounds};
                auto bestPath = path;
                const auto area = [](const RECT& rect) {
                    return int64_t(rect.right - rect.left) * (rect.bottom - rect.top);
                };
                auto bestArea = area(query.window.bounds);
                const auto visit = [&](const auto& visit, IUIAutomationElement* element, RECT parentBounds) -> void {
                    if (self->sequence != query.sequence) return;
                    ComPtr<IUIAutomationElement> cachedParent;
                    ComPtr<IUIAutomationElementArray> children;
                    int count{};
                    if (FAILED(element->BuildUpdatedCache(cache.Get(), &cachedParent)) ||
                        FAILED(cachedParent->GetCachedChildren(&children)) ||
                        !children || FAILED(children->get_Length(&count))) return;

                    // Chromium can expose overlapping containers with the same bounds; either can own the control.
                    for (int i = count - 1; i >= 0 && self->sequence == query.sequence; --i) {
                        ComPtr<IUIAutomationElement> child;
                        RECT bounds{}, clipped{};
                        BOOL offscreen{};
                        if (FAILED(children->GetElement(i, &child)) || !child ||
                            FAILED(child->get_CachedIsOffscreen(&offscreen)) || offscreen ||
                            FAILED(child->get_CachedBoundingRectangle(&bounds)) ||
                            !IntersectRect(&clipped, &bounds, &parentBounds) || !PtInRect(&clipped, query.point))
                            continue;

                        const bool narrower = !EqualRect(&clipped, &parentBounds);
                        if (narrower) path.push_back(clipped);
                        const auto childArea = area(clipped);
                        if (childArea < bestArea) {
                            bestArea = childArea;
                            bestPath = path;
                        }
                        visit(visit, child.Get(), clipped);
                        if (narrower) path.pop_back();
                    }
                };
                if (parent) visit(visit, parent.Get(), query.window.bounds);

                if (self->sequence != query.sequence) continue;
                self->dispatcher.TryEnqueue([self, sequence = query.sequence, from = query.descendFrom, path = std::move(bestPath)]() mutable {
                    if (self->sequence != sequence) return;
                    auto selection = path.size() - 1;
                    if (from) {
                        const auto parent = std::find_if(path.begin(), path.end(), [&](const RECT& bounds) {
                            return EqualRect(&bounds, &*from);
                        });
                        if (parent == path.end()) return;
                        selection = std::min(selection, static_cast<size_t>(parent - path.begin()) + 1);
                    }
                    self->path = std::move(path);
                    self->level = selection;
                    self->show(self->path[selection]);
                });
            }
        }
    }
    CoUninitialize();
}

HoverSelection::HoverSelection(RECT desktop, std::function<void(RECT)> show)
    : state(std::make_shared<State>(desktop, std::move(show)))
{
    // A provider may still be answering when the user closes the overlay. The worker owns its state until it returns.
    std::thread(State::run, state).detach();
}

HoverSelection::~HoverSelection()
{
    {
        std::scoped_lock lock(state->gate);
        state->stopping = true;
        ++state->sequence;
    }
    state->wake.notify_one();
}

void HoverSelection::move(POINT screen)
{
    auto& s = *state;
    if (s.locked && PtInRect(&s.path[s.level], screen)) return;
    if (!s.locked && s.hovered && s.point.x == screen.x && s.point.y == screen.y) return;
    s.locked = false;
    s.point = screen;
    const auto hit = std::find_if(s.windows.begin(), s.windows.end(), [screen](const auto& window) {
        return PtInRect(&window.bounds, screen);
    });
    if (hit == s.windows.end()) {
        if (!s.hovered) return;
        freeze();
        s.path.clear();
        s.show({});
        return;
    }
    if (s.hovered != &*hit || !PtInRect(&s.path[s.level], screen)) {
        s.path = {hit->bounds};
        s.level = 0;
        s.show(hit->bounds);
    }
    s.hovered = &*hit;
    s.request(screen);
}

void HoverSelection::wheel(POINT screen, float direction)
{
    auto& s = *state;
    if (!s.hovered || direction == 0 || !PtInRect(&s.path[s.level], screen)) return;
    s.locked = true;
    if (direction > 0) {
        if (s.level > 0) --s.level;
    } else if (s.level + 1 < s.path.size() && PtInRect(&s.path[s.level + 1], screen)) {
        ++s.level;
    } else {
        s.request(screen, s.path[s.level]);
        return;
    }
    s.cancel();
    s.show(s.path[s.level]);
}

void HoverSelection::freeze()
{
    state->cancel();
    state->hovered = nullptr;
    state->locked = false;
}
