#include "MouseMode.h"
#include <shellapi.h>
#include <objbase.h>
#include <thread>

// ============================================================================
// 手把滑鼠模式（Gamepad Mouse Mode） — 查 Bindings 表
// ============================================================================
//
// 本模組直接呼叫 SendInput()。
// ============================================================================

namespace {

    // ── 常數 ──────────────────────────────────────────────────────────────────

    constexpr float kDeadzone = 0.08f;

    // 游標速度（@ 100%）：線性，推多少動多少
    constexpr float kMaxSpeed = 18.0f;

    // 滾輪累積：每 tick 推進 kWheelStep，達 kWheelTriggerDelta 觸發一次系統事件
    // WHEEL_DELTA 是 Windows 系統巨集（120），故此處改名避免衝突
    constexpr float kWheelStep         = 25.0f;
    constexpr float kWheelTriggerDelta = 120.0f;

    // Trigger 邊緣觸發臨界值
    constexpr BYTE kTriggerThreshold = 128;

    // 搖桿最大值（XInput SHORT 範圍）
    constexpr float kStickMax = 32767.0f;

    // 搖桿視為「方向按下」的量化臨界
    constexpr float kDirectionThreshold = 0.5f;

    // ── 內部狀態 ────────────────────────────────────────────────────────────────

    float cursorAccumX = 0.0f;
    float cursorAccumY = 0.0f;
    float wheelAccumX  = 0.0f;
    float wheelAccumY  = 0.0f;

    WORD  prevButtons = 0;
    BYTE  prevLT = 0, prevRT = 0;

    // 鍵盤鍵目前「壓著」狀態（KeyHold / KeyTap 鏡像按住用；KeyCombo 永遠 tap 不寫入此表）
    struct HeldKey {
        bool active = false;
        WORD vk     = 0;
    };

    // 14 個按鈕類輸入位（A, B, X, Y, LB, RB, LT, RT, LS, RS + DPad 4 向；Stick 不走此表）
    HeldKey heldButton[14] = {};

    // 方向組按住狀態：DPad 整組預設 / LStick / RStick 三組各 4 方向（up/down/left/right）
    // firstDownMs/lastDownMs 給「補 keydown」用：長按期間每隔固定時間重送一次 WM_KEYDOWN
    // 只認 WM_KEYDOWN 事件的前景 App（瀏覽器/Epic）才會持續推進
    struct DirectionalState {
        bool      active      = false;
        ULONGLONG firstDownMs = 0;
        ULONGLONG lastDownMs  = 0;
    };
    enum RepeatGroup { RG_DPad = 0, RG_LStick, RG_RStick, RG_Count };
    DirectionalState directionalState[RG_Count][4] = {};

    // 讀 OS「鍵盤內容」設定推算補 keydown 節奏（首次按下後等 X ms 才開始補、之後每 Y ms 補一次）
    // SPI_GETKEYBOARDDELAY：0..3 對應 250/500/750/1000 ms 初始延遲
    // SPI_GETKEYBOARDSPEED：0..31 線性對應 2.5..30 Hz 重複速率
    // 與真鍵盤長按自動重複採用同一組參數
    void GetKeyboardRepeatTiming(ULONGLONG& outInitialMs, ULONGLONG& outIntervalMs) {
        int delayIdx = 1;  // SPI 失敗時的回退值，對應 500ms（Windows 11 OOBE 常見值）
        int speedIdx = 31; // SPI 失敗時的回退值，對應約 30 Hz（同上）
        SystemParametersInfoW(SPI_GETKEYBOARDDELAY, 0, &delayIdx, 0);
        SystemParametersInfoW(SPI_GETKEYBOARDSPEED, 0, &speedIdx, 0);
        outInitialMs = (ULONGLONG)(250 + delayIdx * 250);
        double rateHz = 2.5 + speedIdx * (27.5 / 31.0);
        outIntervalMs = (ULONGLONG)(1000.0 / rateHz);
    }

    // ── 工具函式：input 送出 ─────────────────────────────────────────────────

    void SendMouseMove(int dx, int dy) {
        if (dx == 0 && dy == 0) return;
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dx = dx;
        in.mi.dy = dy;
        in.mi.dwFlags = MOUSEEVENTF_MOVE;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendMouseWheel(int delta, bool horizontal) {
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.mouseData = (DWORD)delta;
        in.mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendMouseButtonDownUp(MouseWhich which, bool down) {
        DWORD flag = 0;
        switch (which) {
            case MouseWhich::Left:   flag = down ? MOUSEEVENTF_LEFTDOWN   : MOUSEEVENTF_LEFTUP;   break;
            case MouseWhich::Right:  flag = down ? MOUSEEVENTF_RIGHTDOWN  : MOUSEEVENTF_RIGHTUP;  break;
            case MouseWhich::Middle: flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
        }
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dwFlags = flag;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendVKDownUp(WORD vk, bool down) {
        INPUT in = {};
        in.type = INPUT_KEYBOARD;
        in.ki.wVk = vk;
        in.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendVKTap(WORD vk) {
        INPUT in[2] = {};
        in[0].type = INPUT_KEYBOARD;  in[0].ki.wVk = vk;
        in[1].type = INPUT_KEYBOARD;  in[1].ki.wVk = vk;  in[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, in, sizeof(INPUT));
    }

    // 送出組合鍵：依序按下 mods → 按放 vk → 逆序放開 mods（永遠 tap）
    void SendVKCombo(const std::vector<WORD>& mods, WORD vk) {
        const size_t n = mods.size();
        std::vector<INPUT> in(n * 2 + 2);
        size_t i = 0;
        for (size_t m = 0; m < n; ++m) {
            in[i].type = INPUT_KEYBOARD;  in[i].ki.wVk = mods[m];  ++i;
        }
        in[i].type = INPUT_KEYBOARD;  in[i].ki.wVk = vk;  ++i;
        in[i].type = INPUT_KEYBOARD;  in[i].ki.wVk = vk;  in[i].ki.dwFlags = KEYEVENTF_KEYUP;  ++i;
        for (size_t m = 0; m < n; ++m) {
            in[i].type = INPUT_KEYBOARD;  in[i].ki.wVk = mods[n - 1 - m];  in[i].ki.dwFlags = KEYEVENTF_KEYUP;  ++i;
        }
        SendInput((UINT)in.size(), in.data(), sizeof(INPUT));
    }

    // 將 XInput SHORT (-32768~32767) 正規化為 -1.0~1.0，套用圓形死區重縮放
    void NormalizeStick(SHORT rawX, SHORT rawY, float& nx, float& ny, float& mag) {
        float fx = (float)rawX / kStickMax;
        float fy = (float)rawY / kStickMax;
        float m = sqrtf(fx * fx + fy * fy);
        if (m < kDeadzone) {
            nx = ny = 0.0f;
            mag = 0.0f;
            return;
        }
        float scaled = (m - kDeadzone) / (1.0f - kDeadzone);
        if (scaled > 1.0f) scaled = 1.0f;
        nx = fx / m;
        ny = fy / m;
        mag = scaled;
    }

    // ── 子處理函式：搖桿 → 游標 / 滾輪 ─────────────────────────────────────────

    void HandleCursor(SHORT rawX, SHORT rawY, int speedPercent) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) {
            cursorAccumX = cursorAccumY = 0.0f;
            return;
        }
        float speed = mag * kMaxSpeed * speedPercent / 100.0f;
        cursorAccumX += nx * speed;
        cursorAccumY += -ny * speed;  // Y 軸反轉
        int dx = (int)cursorAccumX;
        int dy = (int)cursorAccumY;
        cursorAccumX -= dx;
        cursorAccumY -= dy;
        SendMouseMove(dx, dy);
    }

    void HandleScroll(SHORT rawX, SHORT rawY) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) return;
        wheelAccumX += nx * mag * kWheelStep;
        wheelAccumY += ny * mag * kWheelStep;
        while (wheelAccumY >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, false);  wheelAccumY -= kWheelTriggerDelta; }
        while (wheelAccumY <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, false); wheelAccumY += kWheelTriggerDelta; }
        while (wheelAccumX >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, true);   wheelAccumX -= kWheelTriggerDelta; }
        while (wheelAccumX <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, true);  wheelAccumX += kWheelTriggerDelta; }
    }

    // ── 子處理函式：按鈕類 Action 執行（A..RS / LT / RT） ─────────────────────

    // index 對應 heldButton[] 的 14 個按鈕位
    // 採用：A=0 B=1 X=2 Y=3 LB=4 RB=5 LT=6 RT=7 LS=8 RS=9
    //       DPadUp=10 DPadDown=11 DPadLeft=12 DPadRight=13
    int ButtonHeldIndex(KeyId k) {
        switch (k) {
            case KeyId::A:         return 0;
            case KeyId::B:         return 1;
            case KeyId::X:         return 2;
            case KeyId::Y:         return 3;
            case KeyId::LB:        return 4;
            case KeyId::RB:        return 5;
            case KeyId::LT:        return 6;
            case KeyId::RT:        return 7;
            case KeyId::LS:        return 8;
            case KeyId::RS:        return 9;
            case KeyId::DPadUp:    return 10;
            case KeyId::DPadDown:  return 11;
            case KeyId::DPadLeft:  return 12;
            case KeyId::DPadRight: return 13;
            default:               return -1;
        }
    }

    // 結束「壓著」狀態：依當初按下的種類送對應 keyup / mouseup
    void ReleaseHeldButton(KeyId k, const Action& a) {
        int idx = ButtonHeldIndex(k);
        if (idx < 0) return;
        HeldKey& h = heldButton[idx];
        if (!h.active) return;
        switch (a.kind) {
            case ActionKind::KeyTap:
            case ActionKind::KeyHold:
                if (h.vk) SendVKDownUp(h.vk, false);
                break;
            case ActionKind::MouseButton:
                SendMouseButtonDownUp(a.which, false);
                break;
            default:
                break;
        }
        h = {};
    }

    // 開合螢幕鍵盤（觸控鍵盤 / osk.exe）；定義於後方
    void ToggleTouchKeyboard(VkbMethod method);

    // 邊緣觸發：依 ActionKind 推進，僅在 changed=true 時動作
    // changed=true 表示此 tick 按鈕狀態翻轉；down=true 表示翻轉後為按下
    void ExecuteButtonAction(KeyId k, const Action& a, bool down, bool changed) {
        int idx = ButtonHeldIndex(k);
        if (idx < 0) return;
        HeldKey& h = heldButton[idx];

        switch (a.kind) {
            case ActionKind::None:
                return;

            case ActionKind::KeyTap:
                // 鏡像按住：按下 keydown、放開 keyup
                if (changed) {
                    if (down) {
                        if (a.vk) { SendVKDownUp(a.vk, true); h.active = true; h.vk = a.vk; }
                    } else {
                        if (h.active && h.vk) SendVKDownUp(h.vk, false);
                        h = {};
                    }
                }
                return;

            case ActionKind::KeyHold:
                if (changed) {
                    if (down) {
                        if (a.vk) { SendVKDownUp(a.vk, true); h.active = true; h.vk = a.vk; }
                    } else {
                        if (h.active && h.vk) SendVKDownUp(h.vk, false);
                        h = {};
                    }
                }
                return;

            case ActionKind::KeyCombo:
                // 組合鍵永遠 tap
                if (changed && down && a.vk) SendVKCombo(a.mods, a.vk);
                return;

            case ActionKind::MouseButton:
                if (changed) {
                    SendMouseButtonDownUp(a.which, down);
                    h.active = down;
                }
                return;

            case ActionKind::MouseWheel:
                if (changed && down) {
                    int  delta      = (int)kWheelTriggerDelta;
                    bool horizontal = false;
                    switch (a.dir) {
                        case WheelDir::Up:    delta = +(int)kWheelTriggerDelta; horizontal = false; break;
                        case WheelDir::Down:  delta = -(int)kWheelTriggerDelta; horizontal = false; break;
                        case WheelDir::Left:  delta = -(int)kWheelTriggerDelta; horizontal = true;  break;
                        case WheelDir::Right: delta = +(int)kWheelTriggerDelta; horizontal = true;  break;
                    }
                    SendMouseWheel(delta, horizontal);
                }
                return;

            case ActionKind::TouchKeyboard:
                // 邊緣觸發：按下時開合螢幕鍵盤一次
                if (changed && down) ToggleTouchKeyboard(a.vkb);
                return;

            default:
                return;
        }
    }

    // ── 子處理函式：方向鍵 / WASD 量化 ───────────────────────────────────────

    // up / down / left / right
    static const WORD kArrowVks[4] = { VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT };
    static const WORD kWasdVks[4]  = { 'W',   'S',     'A',     'D' };

    // 將搖桿/十字鍵 4 方向的按下狀態送出：首發 keydown、放開時送 keyup
    // repeatKeyDown=true：持續按住期間依 OS 鍵盤重複設定補 keydown，讓只認 WM_KEYDOWN 事件的
    //                     導覽類前景（瀏覽器/Epic 等）持續推進
    // repeatKeyDown=false：純鏡像按住。實測：遊戲端（讀 GetAsyncKeyState/DirectInput）若收到補 keydown，
    //                      角色長按移動會卡動作、選單操作會雙觸發
    void ApplyDirectional(RepeatGroup group, const bool pressed[4], const WORD vks[4], bool repeatKeyDown) {
        ULONGLONG initialMs = 0, intervalMs = 0;
        if (repeatKeyDown) GetKeyboardRepeatTiming(initialMs, intervalMs);
        ULONGLONG now = GetTickCount64();
        for (int i = 0; i < 4; ++i) {
            DirectionalState& s = directionalState[group][i];
            if (pressed[i]) {
                if (!s.active) {
                    SendVKDownUp(vks[i], true);
                    s.active      = true;
                    s.firstDownMs = now;
                    s.lastDownMs  = now;
                } else if (repeatKeyDown &&
                           now - s.firstDownMs >= initialMs &&
                           now - s.lastDownMs  >= intervalMs) {
                    SendVKDownUp(vks[i], true);
                    s.lastDownMs = now;
                }
            } else if (s.active) {
                SendVKDownUp(vks[i], false);
                s = {};
            }
        }
    }

    // 由 D-pad button mask 推 4 方向按下狀態
    void DpadPressedFromButtons(WORD buttons, bool out[4]) {
        out[0] = (buttons & XINPUT_GAMEPAD_DPAD_UP)    != 0;
        out[1] = (buttons & XINPUT_GAMEPAD_DPAD_DOWN)  != 0;
        out[2] = (buttons & XINPUT_GAMEPAD_DPAD_LEFT)  != 0;
        out[3] = (buttons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
    }

    // 由搖桿原始值推 4 方向按下狀態（量化）
    void StickPressedFromRaw(SHORT rawX, SHORT rawY, bool out[4]) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        float vx = nx * mag, vy = ny * mag;
        out[0] = vy >=  kDirectionThreshold;   // up
        out[1] = vy <= -kDirectionThreshold;   // down
        out[2] = vx <= -kDirectionThreshold;   // left
        out[3] = vx >=  kDirectionThreshold;   // right
    }

    // ── RunBindings：每 tick 主流程 ──────────────────────────────────────────

    // KeyId → XInput button mask（按鈕類；LT/RT/搖桿類另走專用路徑）
    WORD XInputMaskOf(KeyId k) {
        switch (k) {
            case KeyId::A:         return XINPUT_GAMEPAD_A;
            case KeyId::B:         return XINPUT_GAMEPAD_B;
            case KeyId::X:         return XINPUT_GAMEPAD_X;
            case KeyId::Y:         return XINPUT_GAMEPAD_Y;
            case KeyId::LB:        return XINPUT_GAMEPAD_LEFT_SHOULDER;
            case KeyId::RB:        return XINPUT_GAMEPAD_RIGHT_SHOULDER;
            case KeyId::LS:        return XINPUT_GAMEPAD_LEFT_THUMB;
            case KeyId::RS:        return XINPUT_GAMEPAD_RIGHT_THUMB;
            case KeyId::DPadUp:    return XINPUT_GAMEPAD_DPAD_UP;
            case KeyId::DPadDown:  return XINPUT_GAMEPAD_DPAD_DOWN;
            case KeyId::DPadLeft:  return XINPUT_GAMEPAD_DPAD_LEFT;
            case KeyId::DPadRight: return XINPUT_GAMEPAD_DPAD_RIGHT;
            default:               return 0;
        }
    }

    // 若 DPad 4 個 KeyId 都是 KeyTap 且 vk 非 0，
    // 回 true 並將 4 個 vk 依 up/down/left/right 順序填入 outVks。否則回 false，DPad 走逐鍵 dispatch。
    bool TryGetDPadDirectionalVks(const Bindings& bindings, WORD outVks[4]) {
        const Action& up    = At(bindings, KeyId::DPadUp);
        const Action& down  = At(bindings, KeyId::DPadDown);
        const Action& left  = At(bindings, KeyId::DPadLeft);
        const Action& right = At(bindings, KeyId::DPadRight);
        if (up.kind    != ActionKind::KeyTap || up.vk    == 0) return false;
        if (down.kind  != ActionKind::KeyTap || down.vk  == 0) return false;
        if (left.kind  != ActionKind::KeyTap || left.vk  == 0) return false;
        if (right.kind != ActionKind::KeyTap || right.vk == 0) return false;
        outVks[0] = up.vk;
        outVks[1] = down.vk;
        outVks[2] = left.vk;
        outVks[3] = right.vk;
        return true;
    }

    // ── Layered Mode 狀態機 ──────────────────────────────────────────────────

    // HoldRelease：按住 triggerKey 達此門檻（毫秒）才令 layer 作用
    constexpr ULONGLONG kLayeredHoldMs = 1600;
    // DoubleTapToggle：兩次 tap 須落在此時窗（毫秒）內才算雙擊
    constexpr ULONGLONG kDoubleTapWindowMs = 400;

    struct LayeredRuntime {
        bool      active          = false;  // layer 目前是否作用
        bool      triggerPrevDown = false;  // triggerKey 上一 tick 是否按下
        ULONGLONG holdStartMs     = 0;      // HoldRelease：triggerKey 按下的時刻
        ULONGLONG lastTapMs       = 0;      // DoubleTapToggle：上一次 tap（放開）的時刻
    };
    LayeredRuntime layered = {};

    // 釋放所有「壓著」狀態與方向組、清空游標/滾輪累積（不動 prevButtons / layered）
    void ReleaseHeldInputs() {
        cursorAccumX = cursorAccumY = 0.0f;
        wheelAccumX  = wheelAccumY  = 0.0f;
        for (auto& h : heldButton) {
            if (h.active && h.vk) SendVKDownUp(h.vk, false);
            h = {};
        }
        for (int g = 0; g < RG_Count; ++g)
            for (int i = 0; i < 4; ++i) {
                DirectionalState& s = directionalState[g][i];
                if (s.active) {
                    SendVKDownUp(kArrowVks[i], false);
                    SendVKDownUp(kWasdVks[i],  false);
                }
                s = {};
            }
    }

    // triggerKey 此 tick 是否按下（搖桿擺動類不可作 trigger，一律回 false）
    bool IsKeyIdDown(KeyId k, const XINPUT_GAMEPAD& pad) {
        if (k == KeyId::LT) return pad.bLeftTrigger  >= kTriggerThreshold;
        if (k == KeyId::RT) return pad.bRightTrigger >= kTriggerThreshold;
        WORD mask = XInputMaskOf(k);
        return mask != 0 && (pad.wButtons & mask) != 0;
    }

    // ── Layered Mode 聲音回饋 ────────────────────────────────────────────────
    //
    // 三種情境，各用不同音調：
    //   HoldRelease activate      → 單音 880 Hz (A5, 70 ms)
    //   DoubleTapToggle → ON      → 上行雙音 880→1100 Hz（各 50/70 ms）
    //   DoubleTapToggle → OFF     → 下行雙音 1100→700 Hz（各 50/70 ms）
    //
    // 在獨立執行緒執行，不阻塞主迴圈（Beep() 呼叫本身為同步，但僅影響子執行緒）。
    void PlayLayerSound(bool isToggle, bool nowActive) {
        if (isToggle) {
            if (nowActive)
                std::thread([]{ Beep(880, 50); Beep(1100, 70); }).detach();
            else
                std::thread([]{ Beep(1100, 50); Beep(700, 70); }).detach();
        } else {
            // HoldRelease：僅在進入作用（rising edge）時響一聲
            std::thread([]{ Beep(880, 70); }).detach();
        }
    }

    // 更新 Layered Mode 狀態；profile 啟用 Layered 時每 tick 呼叫
    void UpdateLayeredMode(const GamepadProfile& profile, const XINPUT_GAMEPAD& pad) {
        const bool      down = IsKeyIdDown(profile.layered.triggerKey, pad);
        const ULONGLONG now  = GetTickCount64();

        if (profile.layered.activationMode == LayeredActivationMode::HoldRelease) {
            if (down) {
                if (!layered.triggerPrevDown) layered.holdStartMs = now;  // 剛按下
                layered.active = (now - layered.holdStartMs >= kLayeredHoldMs);
            } else {
                layered.active = false;
            }
        } else {  // DoubleTapToggle
            if (!down && layered.triggerPrevDown) {  // 剛放開 = 一次 tap
                if (layered.lastTapMs != 0 && now - layered.lastTapMs <= kDoubleTapWindowMs) {
                    layered.active    = !layered.active;  // 雙擊命中 → 切換
                    layered.lastTapMs = 0;
                } else {
                    layered.lastTapMs = now;
                }
            }
        }
        layered.triggerPrevDown = down;
    }

    // ── 螢幕鍵盤開合 ─────────────────────────────────────────────────────────
    //
    // 兩種方式：
    //   Osk → ShellExecute osk.exe（傳統螢幕鍵盤；簡單可靠，不會偵測手把佈局）
    //   Com → 透過未公開 ITipInvocation COM 介面切換 TabTip 觸控鍵盤
    //         （Windows 11 接到手把時可能自動切換到遊戲控制器佈局）
    //
    // 註：OmniConsole 主程式（UWP）另有 CoreInputView.TryShow(Gamepad) 走現代手把鍵盤；
    // PhantomKey 為純 Win32 行程無 CoreApplicationView 無法直接呼叫，僅能走上述兩條路徑。

    // UIHostNoLaunch CLSID + ITipInvocation IID（未公開 API；用 TabTip 觸控鍵盤）
    struct __declspec(uuid("37c994e7-432b-4834-a2f7-dce1f13b834b")) ITipInvocation : IUnknown {
        virtual HRESULT STDMETHODCALLTYPE Toggle(HWND wnd) = 0;
    };
    static const CLSID CLSID_UIHostNoLaunch =
        { 0x4CE576FA, 0x83DC, 0x4F88, { 0x95, 0x1C, 0x9D, 0x07, 0x82, 0xB4, 0xE3, 0x76 } };

    static void ToggleTabTipCom() {
        // 主迴圈執行緒的 COM 初始化；同模式重複呼叫回 S_FALSE、不同模式回 RPC_E_CHANGED_MODE，
        // 兩種情境皆視為已初始化。
        ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

        ITipInvocation* tip = nullptr;
        HRESULT hr = ::CoCreateInstance(
            CLSID_UIHostNoLaunch, nullptr,
            CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER,
            __uuidof(ITipInvocation), reinterpret_cast<void**>(&tip));
        if (SUCCEEDED(hr) && tip) {
            tip->Toggle(::GetDesktopWindow());
            tip->Release();
        }
    }

    void ToggleTouchKeyboard(VkbMethod method) {
        if (method == VkbMethod::Osk) {
            ::ShellExecuteW(nullptr, L"open", L"osk.exe", nullptr, nullptr, SW_SHOWNORMAL);
            return;
        }
        // VkbMethod::Com → TabTip
        ToggleTabTipCom();
    }

    void RunBindings(const XINPUT_GAMEPAD& pad, const Bindings& bindings,
                     int cursorSpeedPercent, bool skipDpad, bool repeatKeyDown) {
        const WORD buttons = pad.wButtons;
        const WORD changedMask = buttons ^ prevButtons;

        // 按鈕類：A..RS（8 個；DPad 4 個獨立路徑處理，不在此表）
        static const KeyId kBtnKeys[] = {
            KeyId::A, KeyId::B, KeyId::X, KeyId::Y,
            KeyId::LB, KeyId::RB, KeyId::LS, KeyId::RS
        };
        for (KeyId k : kBtnKeys) {
            const WORD mask = XInputMaskOf(k);
            const bool changed = (changedMask & mask) != 0;
            const bool down    = (buttons & mask) != 0;
            ExecuteButtonAction(k, At(bindings, k), down, changed);
        }

        // 板機類：LT / RT — 類比過 threshold 視為按下
        {
            const bool ltDown   = pad.bLeftTrigger  >= kTriggerThreshold;
            const bool ltPrev   = prevLT            >= kTriggerThreshold;
            const bool ltChange = ltDown != ltPrev;
            ExecuteButtonAction(KeyId::LT, At(bindings, KeyId::LT), ltDown, ltChange);

            const bool rtDown   = pad.bRightTrigger >= kTriggerThreshold;
            const bool rtPrev   = prevRT            >= kTriggerThreshold;
            const bool rtChange = rtDown != rtPrev;
            ExecuteButtonAction(KeyId::RT, At(bindings, KeyId::RT), rtDown, rtChange);
        }

        // 十字鍵 4 鍵：若 4 鍵都是 KeyTap → 整組走 ApplyDirectional 鏡像按住；
        //              否則 → 逐鍵 ExecuteButtonAction（自訂模式可任意 ActionKind）
        {
            static const KeyId kDPadKeys[4] = {
                KeyId::DPadUp, KeyId::DPadDown, KeyId::DPadLeft, KeyId::DPadRight
            };
            if (skipDpad) {
                // 前景對 D-pad 已有原生反應 → 不送並清狀態
                for (auto& s : directionalState[RG_DPad]) s = {};
                for (KeyId k : kDPadKeys) {
                    int idx = ButtonHeldIndex(k);
                    if (idx >= 0 && heldButton[idx].active && heldButton[idx].vk) {
                        SendVKDownUp(heldButton[idx].vk, false);
                        heldButton[idx] = {};
                    }
                }
            } else {
                WORD vks[4] = {};
                if (TryGetDPadDirectionalVks(bindings, vks)) {
                    bool pressed[4] = {};
                    DpadPressedFromButtons(buttons, pressed);
                    ApplyDirectional(RG_DPad, pressed, vks, repeatKeyDown);
                } else {
                    for (auto& s : directionalState[RG_DPad]) s = {};
                    for (KeyId k : kDPadKeys) {
                        const WORD mask = XInputMaskOf(k);
                        const bool changed = (changedMask & mask) != 0;
                        const bool down    = (buttons & mask) != 0;
                        ExecuteButtonAction(k, At(bindings, k), down, changed);
                    }
                }
            }
        }

        // 左 / 右搖桿
        struct StickSpec { KeyId id; RepeatGroup group; SHORT rawX, rawY; };
        const StickSpec sticks[2] = {
            { KeyId::LStick, RG_LStick, pad.sThumbLX, pad.sThumbLY },
            { KeyId::RStick, RG_RStick, pad.sThumbRX, pad.sThumbRY }
        };
        for (const auto& s : sticks) {
            const Action& a = At(bindings, s.id);
            switch (a.kind) {
                case ActionKind::None:
                    for (auto& r : directionalState[s.group]) r = {};
                    break;
                case ActionKind::StickCursor:
                    HandleCursor(s.rawX, s.rawY, cursorSpeedPercent);
                    for (auto& r : directionalState[s.group]) r = {};
                    break;
                case ActionKind::StickScroll:
                    HandleScroll(s.rawX, s.rawY);
                    for (auto& r : directionalState[s.group]) r = {};
                    break;
                case ActionKind::StickArrows:
                case ActionKind::StickWasd: {
                    bool pressed[4] = {};
                    StickPressedFromRaw(s.rawX, s.rawY, pressed);
                    const WORD* vks = (a.kind == ActionKind::StickWasd) ? kWasdVks : kArrowVks;
                    ApplyDirectional(s.group, pressed, vks, repeatKeyDown);
                    break;
                }
                default:
                    break;
            }
        }

        prevButtons = buttons;
        prevLT = pad.bLeftTrigger;
        prevRT = pad.bRightTrigger;
    }

}  // anonymous namespace

// ============================================================================
// 公開介面
// ============================================================================

namespace MouseMode {

    void Tick(const XINPUT_GAMEPAD& pad, const GamepadProfile& profile, bool skipDpad) {
        if (profile.layered.enabled) {
            const bool wasActive = layered.active;
            UpdateLayeredMode(profile, pad);

            // 狀態改變時播放聲音回饋
            if (layered.active != wasActive) {
                const bool isToggle =
                    (profile.layered.activationMode == LayeredActivationMode::DoubleTapToggle);
                // HoldRelease 只在 activate（rising edge）時響；Toggle 兩端都響
                if (isToggle || layered.active)
                    PlayLayerSound(isToggle, layered.active);
            }

            if (!layered.active) {
                // layer 未作用：不送出映射。剛從作用切回未作用時釋放壓著狀態；
                // 維持輸入基準（prevButtons 等）為現值，避免 layer 重新作用時誤判邊緣。
                if (wasActive) ReleaseHeldInputs();
                prevButtons = pad.wButtons;
                prevLT      = pad.bLeftTrigger;
                prevRT      = pad.bRightTrigger;
                return;
            }
            // layer 作用中：套用映射，但 triggerKey 保留作切換用，其映射不送出
            Bindings b = profile.bindings;
            At(b, profile.layered.triggerKey) = ActNone();
            RunBindings(pad, b, profile.cursorSpeedPercent, skipDpad, profile.dpadAutoRepeat);
            return;
        }
        RunBindings(pad, profile.bindings, profile.cursorSpeedPercent, skipDpad, profile.dpadAutoRepeat);
    }

    void Reset() {
        ReleaseHeldInputs();
        prevButtons = 0;
        prevLT = prevRT = 0;
        layered = {};
    }

}  // namespace MouseMode
