#include "MouseMode.h"
#include "Log.h"
#include <vector>
#include <algorithm>
#include <thread>
#include <objbase.h>
#include <shellapi.h>
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

// ============================================================================
// 手把滑鼠模式（Gamepad Mouse Mode）
// ============================================================================
//
// v2.x: Mapping table-driven + Layered Mode state machine.
// Mapping per tombol diparsing dari config (Shared.ini) menjadi struktur
// ParsedMapping, lalu di-dispatch saat tombol di-press / release.
//
// Layered Mode: jika config.layeredEnabled, semua mapping HANYA aktif setelah
// trigger button ditahan ≥ 3 detik. Saat dilepas, layer non-aktif lagi.
//
// 注意：本模組直接呼叫 SendInput()，不重用 InputSender::SendKeyCombo
// （SendKeyCombo 在按下與放開間有 Sleep(50)，不適合 Mouse Mode 高頻使用）。
// ============================================================================

namespace {

    // ── 常數 ──────────────────────────────────────────────────────────────────

    constexpr float kDeadzone = 0.05f;
    constexpr float kMaxSpeed = 20.0f;
    constexpr float kWheelStep         = 20.0f;  // was 8 — faster scroll
    constexpr float kWheelTriggerDelta = 130.0f;
    constexpr ULONGLONG kLongPressInitialMs = 400;
    constexpr ULONGLONG kLongPressRepeatMs  = 50;
    constexpr BYTE kTriggerThreshold = 128;
    constexpr float kStickMax = 32767.0f;

    // Layered Mode: tahan trigger ≥ 3 detik untuk aktifkan. (3000 - kLongPressInitialMs)
    constexpr ULONGLONG kLayeredHoldMs = 2600;

    // ── 內部狀態 ────────────────────────────────────────────────────────────────

    float cursorAccumX = 1.5f;
    float cursorAccumY = 1.5f;
    float wheelAccumX = 0.0f;
    float wheelAccumY = 2.0f;
    WORD  prevButtons = 0;
    BYTE  prevLT = 0, prevRT = 0;

    struct RepeatState {
        bool active;
        ULONGLONG firstFireMs;
        ULONGLONG lastFireMs;
    };

    RepeatState dpadRepeat[4] = {};

    // Layered Mode runtime state (independen layout — di-reset saat config berubah).
    struct LayeredRuntime {
        bool      triggerHeld    = false;
        ULONGLONG triggerStartMs = 0;
        bool      layerActive    = false;
        // State terakhir untuk mouse-hold mappings: jika user lepas trigger
        // saat lclick/rclick masih "down", kita harus release agar tidak
        // tertinggal stuck.
        bool mouseLeftHeld   = false;
        bool mouseRightHeld  = false;
        bool mouseMiddleHeld = false;
    };
    LayeredRuntime layered = {};

    // ── Mapping parsing ───────────────────────────────────────────────────────

    enum MapAction : int {
        MAP_NONE = 0,
        MAP_KEY,            // Tap key (with up to 2 modifiers)
        MAP_MOUSE_LEFT,     // Press-and-hold
        MAP_MOUSE_RIGHT,    // Press-and-hold
        MAP_MOUSE_MIDDLE,   // Press-and-hold
        MAP_WHEEL_UP,       // Single wheel tick
        MAP_WHEEL_DOWN,
        MAP_WHEEL_LEFT,
        MAP_WHEEL_RIGHT,
        MAP_VKB_COM,        // Touch keyboard via ITipInvocation::Toggle() COM
        MAP_VKB_OSK,        // On-Screen Keyboard (osk.exe)
    };

    struct ParsedMapping {
        MapAction action = MAP_NONE;
        WORD modifiers[2] = { 0, 0 };
        int  modCount = 0;
        WORD vk = 0;
    };

    // Trim whitespace dan lowercase token in-place.
    void NormalizeToken(std::wstring& t) {
        while (!t.empty() && (t.front() == L' ' || t.front() == L'\t')) t.erase(t.begin());
        while (!t.empty() && (t.back() == L' '  || t.back() == L'\t'))  t.pop_back();
        for (auto& c : t) c = towlower(c);
    }

    // Parse satu token non-modifier ke VK code. Return 0 jika tidak dikenali.
    WORD ParseKeyToken(const std::wstring& tok) {
        // Special keys
        if (tok == L"tab")        return VK_TAB;
        if (tok == L"escape" || tok == L"esc") return VK_ESCAPE;
        if (tok == L"enter" || tok == L"return") return VK_RETURN;
        if (tok == L"space")      return VK_SPACE;
        if (tok == L"backspace")  return VK_BACK;
        if (tok == L"home")       return VK_HOME;
        if (tok == L"end")        return VK_END;
        if (tok == L"insert")     return VK_INSERT;
        if (tok == L"delete" || tok == L"del") return VK_DELETE;
        if (tok == L"pageup"   || tok == L"pgup") return VK_PRIOR;
        if (tok == L"pagedown" || tok == L"pgdn") return VK_NEXT;
        if (tok == L"up")    return VK_UP;
        if (tok == L"down")  return VK_DOWN;
        if (tok == L"left")  return VK_LEFT;
        if (tok == L"right") return VK_RIGHT;
        // F1..F12
        if (tok.size() >= 2 && tok[0] == L'f') {
            int n = _wtoi(tok.c_str() + 1);
            if (n >= 1 && n <= 12) return (WORD)(VK_F1 + n - 1);
        }
        // A..Z (single char)
        if (tok.size() == 1 && tok[0] >= L'a' && tok[0] <= L'z')
            return (WORD)(0x41 + (tok[0] - L'a'));
        // 0..9
        if (tok.size() == 1 && tok[0] >= L'0' && tok[0] <= L'9')
            return (WORD)(0x30 + (tok[0] - L'0'));
        return 0;
    }

    // Parse mapping string seperti "ctrl+shift+tab", "lclick", "wheelup", "".
    ParsedMapping ParseMapping(const std::wstring& s) {
        ParsedMapping m = {};
        if (s.empty()) return m;

        // Tokenize by '+'
        std::vector<std::wstring> toks;
        std::wstring cur;
        for (size_t i = 0; i <= s.size(); i++) {
            wchar_t c = (i < s.size()) ? s[i] : L'+';
            if (c == L'+') { toks.push_back(cur); cur.clear(); }
            else cur += c;
        }
        for (auto& t : toks) NormalizeToken(t);
        toks.erase(std::remove_if(toks.begin(), toks.end(),
            [](const std::wstring& x) { return x.empty(); }), toks.end());
        if (toks.empty()) return m;

        // Special action tokens (single token only)
        if (toks.size() == 1) {
            const auto& tok = toks[0];
            if (tok == L"lclick")     { m.action = MAP_MOUSE_LEFT;   return m; }
            if (tok == L"rclick")     { m.action = MAP_MOUSE_RIGHT;  return m; }
            if (tok == L"mclick")     { m.action = MAP_MOUSE_MIDDLE; return m; }
            if (tok == L"wheelup")    { m.action = MAP_WHEEL_UP;     return m; }
            if (tok == L"wheeldown")  { m.action = MAP_WHEEL_DOWN;   return m; }
            if (tok == L"wheelleft")  { m.action = MAP_WHEEL_LEFT;   return m; }
            if (tok == L"wheelright") { m.action = MAP_WHEEL_RIGHT;  return m; }
            if (tok == L"vkb_com")    { m.action = MAP_VKB_COM;      return m; }
            if (tok == L"vkb_osk")    { m.action = MAP_VKB_OSK;      return m; }
            if (tok == L"none")       { return m; }
        }

        // Parse: modifiers (ctrl/shift/alt) → main key
        for (size_t i = 0; i < toks.size(); i++) {
            const auto& tok = toks[i];
            bool isLast = (i == toks.size() - 1);
            if (!isLast) {
                if (tok == L"ctrl" || tok == L"control") {
                    if (m.modCount < 2) m.modifiers[m.modCount++] = VK_CONTROL;
                } else if (tok == L"shift") {
                    if (m.modCount < 2) m.modifiers[m.modCount++] = VK_SHIFT;
                } else if (tok == L"alt") {
                    if (m.modCount < 2) m.modifiers[m.modCount++] = VK_MENU;
                }
            } else {
                // Last token: bisa main key, atau modifier-only (tak valid)
                if (tok == L"ctrl" || tok == L"control" || tok == L"shift" || tok == L"alt")
                    return m;  // modifier-only → invalid
                m.vk = ParseKeyToken(tok);
                if (m.vk == 0) return m;  // unknown token
                m.action = MAP_KEY;
            }
        }
        return m;
    }

    // ── Virtual keyboard helpers ──────────────────────────────────────────────

    // Method 1: ITipInvocation::Toggle() via COM (works best on touch-capable devices).
    // {4CE576FA-83DC-4F88-951C-9D0782B4E376} / {37C994E7-432B-4834-A2F7-DCE1F13B834B}
    struct ITipInvocation_Vtbl { void* qi; void* addref; void* release; void* toggle; };
    static void LaunchVkbCom() {
        std::thread([]() {
            // Ensure tabtip.exe is registered as COM server
            ShellExecuteW(nullptr, L"open",
                L"C:\\Program Files\\Common Files\\Microsoft Shared\\Ink\\TabTip.exe",
                nullptr, nullptr, SW_HIDE);
            Sleep(120);

            HRESULT hrInit = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

            static const CLSID CLSID_TipInv =
                {0x4CE576FA,0x83DC,0x4F88,{0x95,0x1C,0x9D,0x07,0x82,0xB4,0xE3,0x76}};
            static const IID IID_ITipInv =
                {0x37C994E7,0x432B,0x4834,{0xA2,0xF7,0xDC,0xE1,0xF1,0x3B,0x83,0x4B}};

            // Declare minimal COM interface inline (avoids SDK header dependency)
            struct ITipInv : IUnknown { virtual HRESULT STDMETHODCALLTYPE Toggle(HWND) = 0; };

            ITipInv* pTip = nullptr;
            HRESULT hr = CoCreateInstance(CLSID_TipInv, nullptr,
                CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER,
                IID_ITipInv, reinterpret_cast<void**>(&pTip));
            if (SUCCEEDED(hr) && pTip) {
                pTip->Toggle(GetDesktopWindow());
                pTip->Release();
                Log(L"[MouseMode] VKB_COM: ITipInvocation::Toggle called");
            } else {
                Log(L"[MouseMode] VKB_COM: CoCreateInstance failed hr=0x%08X", (unsigned)hr);
            }
            if (SUCCEEDED(hrInit)) CoUninitialize();
        }).detach();
    }

    // Method 2: On-Screen Keyboard (osk.exe) — accessible keyboard, always available.
    static void LaunchVkbOsk() {
        std::thread([]() {
            // Check if OSK is already open — find its window and close if so (toggle)
            HWND hwndOsk = FindWindowW(L"OSKMainClass", nullptr);
            if (hwndOsk) {
                PostMessageW(hwndOsk, WM_CLOSE, 0, 0);
                Log(L"[MouseMode] VKB_OSK: closing OSK");
            } else {
                ShellExecuteW(nullptr, L"open", L"osk.exe", nullptr, nullptr, SW_SHOW);
                Log(L"[MouseMode] VKB_OSK: launching osk.exe");
            }
        }).detach();
    }

    // ── Send helpers ──────────────────────────────────────────────────────────

    void SendMouseMove(int dx, int dy) {
        if (dx == 0 && dy == 0) return;
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dx = dx; in.mi.dy = dy;
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

    void SendMouseButton(DWORD downFlag, DWORD upFlag, bool down) {
        INPUT in = {};
        in.type = INPUT_MOUSE;
        in.mi.dwFlags = down ? downFlag : upFlag;
        SendInput(1, &in, sizeof(INPUT));
    }

    void SendVKTap(WORD vk) {
        INPUT in[2] = {};
        in[0].type = INPUT_KEYBOARD; in[0].ki.wVk = vk;
        in[1].type = INPUT_KEYBOARD; in[1].ki.wVk = vk; in[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, in, sizeof(INPUT));
    }

    void SendVKCombo(WORD modifier, WORD vk) {
        INPUT in[4] = {};
        in[0].type = INPUT_KEYBOARD; in[0].ki.wVk = modifier;
        in[1].type = INPUT_KEYBOARD; in[1].ki.wVk = vk;
        in[2].type = INPUT_KEYBOARD; in[2].ki.wVk = vk;       in[2].ki.dwFlags = KEYEVENTF_KEYUP;
        in[3].type = INPUT_KEYBOARD; in[3].ki.wVk = modifier; in[3].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(4, in, sizeof(INPUT));
    }

    void SendVKCombo3(WORD mod1, WORD mod2, WORD vk) {
        INPUT in[6] = {};
        in[0].type = INPUT_KEYBOARD; in[0].ki.wVk = mod1;
        in[1].type = INPUT_KEYBOARD; in[1].ki.wVk = mod2;
        in[2].type = INPUT_KEYBOARD; in[2].ki.wVk = vk;
        in[3].type = INPUT_KEYBOARD; in[3].ki.wVk = vk;   in[3].ki.dwFlags = KEYEVENTF_KEYUP;
        in[4].type = INPUT_KEYBOARD; in[4].ki.wVk = mod2; in[4].ki.dwFlags = KEYEVENTF_KEYUP;
        in[5].type = INPUT_KEYBOARD; in[5].ki.wVk = mod1; in[5].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(6, in, sizeof(INPUT));
    }

    void DispatchKey(const ParsedMapping& m) {
        if (m.action != MAP_KEY || m.vk == 0) return;
        if (m.modCount == 0)      SendVKTap(m.vk);
        else if (m.modCount == 1) SendVKCombo(m.modifiers[0], m.vk);
        else                      SendVKCombo3(m.modifiers[0], m.modifiers[1], m.vk);
    }

    // Dispatch press edge: fires key tap / wheel tick / mouse-down.
    void DispatchPress(const ParsedMapping& m) {
        switch (m.action) {
            case MAP_NONE: break;
            case MAP_KEY: DispatchKey(m); break;
            case MAP_MOUSE_LEFT:
                SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, true);
                layered.mouseLeftHeld = true; break;
            case MAP_MOUSE_RIGHT:
                SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, true);
                layered.mouseRightHeld = true; break;
            case MAP_MOUSE_MIDDLE:
                SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, true);
                layered.mouseMiddleHeld = true; break;
            case MAP_WHEEL_UP:    SendMouseWheel((int)kWheelTriggerDelta, false); break;
            case MAP_WHEEL_DOWN:  SendMouseWheel(-(int)kWheelTriggerDelta, false); break;
            case MAP_WHEEL_LEFT:  SendMouseWheel(-(int)kWheelTriggerDelta, true); break;
            case MAP_WHEEL_RIGHT: SendMouseWheel((int)kWheelTriggerDelta, true); break;
            case MAP_VKB_COM:  LaunchVkbCom(); break;
            case MAP_VKB_OSK:  LaunchVkbOsk(); break;
        }
    }

    // Dispatch release edge: only fires for mouse hold actions.
    void DispatchRelease(const ParsedMapping& m) {
        switch (m.action) {
            case MAP_MOUSE_LEFT:
                SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, false);
                layered.mouseLeftHeld = false; break;
            case MAP_MOUSE_RIGHT:
                SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, false);
                layered.mouseRightHeld = false; break;
            case MAP_MOUSE_MIDDLE:
                SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, false);
                layered.mouseMiddleHeld = false; break;
            default: break;
        }
    }

    // Force-release any held mouse buttons (saat layer non-aktif tiba-tiba).
    void ReleaseStuckMouseButtons() {
        if (layered.mouseLeftHeld) {
            SendMouseButton(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, false);
            layered.mouseLeftHeld = false;
        }
        if (layered.mouseRightHeld) {
            SendMouseButton(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, false);
            layered.mouseRightHeld = false;
        }
        if (layered.mouseMiddleHeld) {
            SendMouseButton(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, false);
            layered.mouseMiddleHeld = false;
        }
    }

    // ── Stick handlers ────────────────────────────────────────────────────────

    void NormalizeStick(SHORT rawX, SHORT rawY, float& nx, float& ny, float& mag) {
        float fx = (float)rawX / kStickMax;
        float fy = (float)rawY / kStickMax;
        float m = sqrtf(fx * fx + fy * fy);
        if (m < kDeadzone) { nx = ny = 0.0f; mag = 0.0f; return; }
        float scaled = (m - kDeadzone) / (1.0f - kDeadzone);
        if (scaled > 1.0f) scaled = 1.0f;
        nx = fx / m; ny = fy / m; mag = scaled;
    }

    void HandleCursor(SHORT rawX, SHORT rawY, int speedPercent) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) { cursorAccumX = cursorAccumY = 0.0f; return; }
        float speed = mag * kMaxSpeed * speedPercent / 100.0f;
        cursorAccumX += nx * speed;
        cursorAccumY += -ny * speed;
        int dx = (int)cursorAccumX, dy = (int)cursorAccumY;
        cursorAccumX -= dx; cursorAccumY -= dy;
        SendMouseMove(dx, dy);
    }

    void HandleScroll(SHORT rawX, SHORT rawY) {
        float nx, ny, mag;
        NormalizeStick(rawX, rawY, nx, ny, mag);
        if (mag <= 0.0f) return;
        wheelAccumX += nx * mag * kWheelStep;
        wheelAccumY += ny * mag * kWheelStep;
        while (wheelAccumY >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, false); wheelAccumY -= kWheelTriggerDelta; }
        while (wheelAccumY <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, false); wheelAccumY += kWheelTriggerDelta; }
        while (wheelAccumX >= kWheelTriggerDelta)  { SendMouseWheel((int)kWheelTriggerDelta, true);  wheelAccumX -= kWheelTriggerDelta; }
        while (wheelAccumX <= -kWheelTriggerDelta) { SendMouseWheel(-(int)kWheelTriggerDelta, true); wheelAccumX += kWheelTriggerDelta; }
    }

    // ── Button → idx helpers ─────────────────────────────────────────────────

    // Cek apakah sebuah ButtonIdx sedang ditekan pada bitmask buttons / triggers.
    bool IsButtonDown(int idx, WORD buttons, BYTE lt, BYTE rt) {
        switch (idx) {
            case BTN_A: return (buttons & XINPUT_GAMEPAD_A) != 0;
            case BTN_B: return (buttons & XINPUT_GAMEPAD_B) != 0;
            case BTN_X: return (buttons & XINPUT_GAMEPAD_X) != 0;
            case BTN_Y: return (buttons & XINPUT_GAMEPAD_Y) != 0;
            case BTN_LB: return (buttons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
            case BTN_RB: return (buttons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
            case BTN_LT: return lt >= kTriggerThreshold;
            case BTN_RT: return rt >= kTriggerThreshold;
            case BTN_LSPress: return (buttons & XINPUT_GAMEPAD_LEFT_THUMB) != 0;
            case BTN_RSPress: return (buttons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0;
            case BTN_DPadUp:    return (buttons & XINPUT_GAMEPAD_DPAD_UP) != 0;
            case BTN_DPadDown:  return (buttons & XINPUT_GAMEPAD_DPAD_DOWN) != 0;
            case BTN_DPadLeft:  return (buttons & XINPUT_GAMEPAD_DPAD_LEFT) != 0;
            case BTN_DPadRight: return (buttons & XINPUT_GAMEPAD_DPAD_RIGHT) != 0;
        }
        return false;
    }

    // ── Layered Mode update ───────────────────────────────────────────────────

    void UpdateLayeredMode(int triggerIdx, bool layeredEnabled,
                          WORD buttons, BYTE lt, BYTE rt) {
        if (!layeredEnabled) {
            if (layered.layerActive || layered.triggerHeld) {
                ReleaseStuckMouseButtons();
            }
            layered.triggerHeld = false;
            layered.layerActive = false;
            return;
        }

        bool held = IsButtonDown(triggerIdx, buttons, lt, rt);
        ULONGLONG now = GetTickCount64();

        if (held && !layered.triggerHeld) {
            // Just pressed
            layered.triggerHeld = true;
            layered.triggerStartMs = now;
            layered.layerActive = false;
        } else if (held && layered.triggerHeld && !layered.layerActive) {
            // Hold timer
            if (now - layered.triggerStartMs >= kLayeredHoldMs) {
                layered.layerActive = true;
                Log(L"[MouseMode] Layered mode ACTIVE (held %llums)",
                    (unsigned long long)(now - layered.triggerStartMs));
                // Double-beep on background thread (880 Hz → 1100 Hz, non-blocking)
                std::thread([]() { Beep(880, 90); Sleep(70); Beep(1100, 90); }).detach();
            }
        } else if (!held && layered.triggerHeld) {
            // Just released
            if (layered.layerActive) {
                Log(L"[MouseMode] Layered mode DEACTIVATED (released)");
                ReleaseStuckMouseButtons();
            }
            layered.triggerHeld = false;
            layered.layerActive = false;
        }
    }

    // ── Mapping dispatch (untuk tombol non-D-Pad) ────────────────────────────

    // Dispatch transition state untuk satu tombol non-DPad pada mapping table.
    // - btnIdx: index ke kButtonNames
    // - skipBecauseTrigger: true jika btnIdx adalah trigger Layered Mode (consumed)
    void HandleSimpleButton(int btnIdx,
                            const std::wstring* mappings,
                            WORD buttons, BYTE lt, BYTE rt,
                            bool skipBecauseTrigger,
                            bool gateLayerActive) {
        bool prev = false, cur = false;
        switch (btnIdx) {
            case BTN_A:
                prev = (prevButtons & XINPUT_GAMEPAD_A) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_A) != 0; break;
            case BTN_B:
                prev = (prevButtons & XINPUT_GAMEPAD_B) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_B) != 0; break;
            case BTN_X:
                prev = (prevButtons & XINPUT_GAMEPAD_X) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_X) != 0; break;
            case BTN_Y:
                prev = (prevButtons & XINPUT_GAMEPAD_Y) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_Y) != 0; break;
            case BTN_LB:
                prev = (prevButtons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0; break;
            case BTN_RB:
                prev = (prevButtons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0; break;
            case BTN_LT:
                prev = prevLT >= kTriggerThreshold;
                cur  = lt >= kTriggerThreshold; break;
            case BTN_RT:
                prev = prevRT >= kTriggerThreshold;
                cur  = rt >= kTriggerThreshold; break;
            case BTN_LSPress:
                prev = (prevButtons & XINPUT_GAMEPAD_LEFT_THUMB) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_LEFT_THUMB) != 0; break;
            case BTN_RSPress:
                prev = (prevButtons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0;
                cur  = (buttons & XINPUT_GAMEPAD_RIGHT_THUMB) != 0; break;
        }

        if (skipBecauseTrigger) return;        // Trigger button: consumed
        if (!gateLayerActive) return;          // Gated by layer

        if (cur && !prev) {
            // Press edge
            ParsedMapping m = ParseMapping(mappings[btnIdx]);
            DispatchPress(m);
        } else if (!cur && prev) {
            // Release edge
            ParsedMapping m = ParseMapping(mappings[btnIdx]);
            DispatchRelease(m);
        }
    }

    // D-Pad dengan auto-repeat, sekarang pakai mapping table.
    void HandleDpadMapped(WORD buttons,
                         const std::wstring* mappings,
                         int triggerIdx, bool layeredEnabled, bool gateLayerActive) {
        static const WORD masks[4] = {
            XINPUT_GAMEPAD_DPAD_UP, XINPUT_GAMEPAD_DPAD_DOWN,
            XINPUT_GAMEPAD_DPAD_LEFT, XINPUT_GAMEPAD_DPAD_RIGHT
        };
        static const int idxMap[4] = {
            BTN_DPadUp, BTN_DPadDown, BTN_DPadLeft, BTN_DPadRight
        };

        ULONGLONG now = GetTickCount64();
        for (int i = 0; i < 4; i++) {
            bool pressed = (buttons & masks[i]) != 0;
            bool isTrigger = (layeredEnabled && triggerIdx == idxMap[i]);
            RepeatState& s = dpadRepeat[i];

            if (pressed) {
                if (isTrigger) { s = {}; continue; }   // Consumed
                if (!gateLayerActive) { s = {}; continue; }

                ParsedMapping m = ParseMapping(mappings[idxMap[i]]);
                if (!s.active) {
                    DispatchPress(m);
                    s.active = true;
                    s.firstFireMs = now;
                    s.lastFireMs = now;
                } else if (now - s.firstFireMs >= kLongPressInitialMs &&
                          now - s.lastFireMs  >= kLongPressRepeatMs) {
                    DispatchPress(m);
                    s.lastFireMs = now;
                }
            } else if (s.active) {
                // For press-and-hold mouse mappings, release.
                ParsedMapping m = ParseMapping(mappings[idxMap[i]]);
                DispatchRelease(m);
                s = {};
            }
        }
    }

}  // anonymous namespace

// ============================================================================
// 公開介面
// ============================================================================

namespace MouseMode {

void Tick(const XINPUT_GAMEPAD& pad, const AppConfig& cfg) {
    bool classic = (_wcsicmp(cfg.mouseModeLayout.c_str(), L"Classic") == 0);

    // 搖桿分配
    SHORT cursorX, cursorY, scrollX, scrollY;
    if (classic) {
        cursorX = pad.sThumbRX; cursorY = pad.sThumbRY;
        scrollX = pad.sThumbLX; scrollY = pad.sThumbLY;
    } else {
        cursorX = pad.sThumbLX; cursorY = pad.sThumbLY;
        scrollX = pad.sThumbRX; scrollY = pad.sThumbRY;
    }

    // Resolve mapping table & layered mode untuk layout aktif
    const std::wstring* mappings = classic ? cfg.mapClassic : cfg.mapOmniNav;
    bool layeredEnabled  = classic ? cfg.layeredEnabledClassic : cfg.layeredEnabledOmniNav;
    int  layeredTrigger  = classic ? cfg.layeredButtonClassic  : cfg.layeredButtonOmniNav;

    // Update Layered Mode state machine (3-detik hold timer)
    UpdateLayeredMode(layeredTrigger, layeredEnabled,
                      pad.wButtons, pad.bLeftTrigger, pad.bRightTrigger);

    // Apakah custom mappings boleh fire?
    // - Layered OFF: selalu boleh
    // - Layered ON & layerActive: boleh
    // - Layered ON & !layerActive: TIDAK boleh
    bool gateActive = !layeredEnabled || layered.layerActive;

    // Stik: gating sama (cursor & scroll juga "custom")
    if (gateActive) {
        HandleCursor(cursorX, cursorY, cfg.cursorSpeedPercent);
        HandleScroll(scrollX, scrollY);
    } else {
        cursorAccumX = cursorAccumY = 0.0f;
        wheelAccumX = wheelAccumY = 0.0f;
    }

    // Tombol & D-Pad: dispatch via mapping table
    for (int i = 0; i < BTN_DPadUp; i++) {  // A..RSPress
        bool isTrigger = (layeredEnabled && layeredTrigger == i);
        HandleSimpleButton(i, mappings,
                           pad.wButtons, pad.bLeftTrigger, pad.bRightTrigger,
                           isTrigger, gateActive);
    }
    HandleDpadMapped(pad.wButtons, mappings, layeredTrigger, layeredEnabled, gateActive);

    // Update prev state — selalu, agar edge detection di tick berikutnya benar
    prevButtons = pad.wButtons;
    prevLT = pad.bLeftTrigger;
    prevRT = pad.bRightTrigger;
}

void Reset() {
    cursorAccumX = cursorAccumY = 0.0f;
    wheelAccumX = wheelAccumY = 0.0f;
    prevButtons = 0;
    prevLT = prevRT = 0;
    for (auto& s : dpadRepeat) s = {};
    ReleaseStuckMouseButtons();
    layered = {};
}

}  // namespace MouseMode
