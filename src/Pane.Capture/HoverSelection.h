#pragma once

#include <windows.h>
#include <functional>
#include <memory>

class HoverSelection
{
public:
    HoverSelection(RECT desktop, std::function<void(RECT)> show);
    ~HoverSelection();

    void move(POINT screen);
    void wheel(POINT screen, float direction);
    void freeze();

private:
    struct State;
    std::shared_ptr<State> state;
};
