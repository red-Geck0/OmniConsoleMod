#include "CursorConflict.h"
#include "InputSender.h"
#include "Log.h"
#include <atomic>

namespace CursorConflict {

namespace {

    // ── 判定門檻 ─────────────────────────────────────────────────────────────

    // 搖桿被推動後，多久之內的注入事件才列入計數。
    // 取得夠寬鬆讓「推搖桿 → 對方送出游標事件」這段延遲進得來，又不至於把
    // 放開搖桿後的一般滑鼠注入誤算進來。
    constexpr ULONGLONG kStickGraceMs = 200;

    // 計數窗長度與成立門檻：窗內累積到 kHitsToTrigger 筆才判定成立。
    // 手把轉游標的送出頻率遠高於此（每秒數十筆），單發的注入點擊不會誤觸。
    constexpr ULONGLONG kCountWindowMs = 1000;
    constexpr int       kHitsToTrigger = 6;

    // 判定成立後維持多久（沒有新事件就自動退回未偵測）。
    constexpr ULONGLONG kActiveHoldMs = 3000;

    // SetEnabled 轉交掛鉤執行緒用的 thread message。
    constexpr UINT kMsgSetEnabled = WM_APP + 1;

    // ── 狀態 ────────────────────────────────────────────────────────────────

    std::atomic<ULONGLONG> s_lastStickMs{ 0 };     // 最後一次搖桿推出死區的時刻
    std::atomic<ULONGLONG> s_windowStartMs{ 0 };   // 目前計數窗起點
    std::atomic<int>       s_hitCount{ 0 };        // 目前計數窗內的命中數
    std::atomic<ULONGLONG> s_lastDetectMs{ 0 };    // 最後一次判定成立的時刻

    HHOOK  s_hook = nullptr;        // 僅掛鉤執行緒存取
    DWORD  s_threadId = 0;
    HANDLE s_thread = nullptr;
    HANDLE s_queueReady = nullptr;  // 掛鉤執行緒的訊息佇列建立完成

    // ── 掛鉤 ────────────────────────────────────────────────────────────────
    //
    // 低階掛鉤有逾時限制（LowLevelHooksTimeout，預設 300ms），超時會被系統靜默
    // 取下。此 proc 內只做常數時間的原子運算，不記 log、不碰檔案。
    //
    LRESULT CALLBACK MouseHookProc(int nCode, WPARAM wParam, LPARAM lParam) {
        if (nCode == HC_ACTION &&
            (wParam == WM_MOUSEMOVE || wParam == WM_MOUSEWHEEL || wParam == WM_MOUSEHWHEEL)) {

            const auto* p = reinterpret_cast<const MSLLHOOKSTRUCT*>(lParam);

            // 只看「被注入、且不是我們送的」——我們自己的 SendInput 都帶 kPhantomKeyInputTag
            if ((p->flags & LLMHF_INJECTED) && p->dwExtraInfo != kPhantomKeyInputTag) {
                const ULONGLONG now = GetTickCount64();
                const ULONGLONG lastStick = s_lastStickMs.load(std::memory_order_relaxed);

                // lastStick 為 0（從未推過搖桿）時相減會得到極大值，自然不成立
                if (lastStick != 0 && now - lastStick <= kStickGraceMs) {
                    const ULONGLONG winStart = s_windowStartMs.load(std::memory_order_relaxed);
                    if (now - winStart > kCountWindowMs) {
                        // 開新窗
                        s_windowStartMs.store(now, std::memory_order_relaxed);
                        s_hitCount.store(1, std::memory_order_relaxed);
                    } else if (s_hitCount.fetch_add(1, std::memory_order_relaxed) + 1 >= kHitsToTrigger) {
                        s_lastDetectMs.store(now, std::memory_order_relaxed);
                    }
                }
            }
        }
        return CallNextHookEx(nullptr, nCode, wParam, lParam);
    }

    void ApplyEnabled(bool enabled) {
        if (enabled) {
            if (s_hook) return;
            s_hook = SetWindowsHookExW(WH_MOUSE_LL, MouseHookProc, GetModuleHandleW(nullptr), 0);
            if (!s_hook) {
                Log(L"[CursorConflict] SetWindowsHookExW failed (err=%lu); detection unavailable.",
                    GetLastError());
                return;
            }
            Log(L"[CursorConflict] hook installed.");
        } else {
            if (!s_hook) return;
            UnhookWindowsHookEx(s_hook);
            s_hook = nullptr;
            // 取下時清空判定狀態，避免下次掛上時沿用舊結論
            s_hitCount.store(0, std::memory_order_relaxed);
            s_lastDetectMs.store(0, std::memory_order_relaxed);
            Log(L"[CursorConflict] hook removed.");
        }
    }

    DWORD WINAPI HookThreadProc(LPVOID) {
        // 先建立訊息佇列再放行 Start()：佇列還沒建立就 PostThreadMessage 會以
        // ERROR_INVALID_THREAD_ID 失敗，開機時第一次 SetEnabled 就會靜默掉。
        MSG msg;
        PeekMessageW(&msg, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        if (s_queueReady) SetEvent(s_queueReady);

        while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
            if (msg.hwnd == nullptr && msg.message == kMsgSetEnabled)
                ApplyEnabled(msg.wParam != 0);
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        ApplyEnabled(false);
        return 0;
    }

} // namespace

// ============================================================================
// 公開介面
// ============================================================================

void Start() {
    if (s_thread) return;

    s_queueReady = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    s_thread = CreateThread(nullptr, 0, HookThreadProc, nullptr, 0, &s_threadId);
    if (!s_thread) {
        Log(L"[CursorConflict] CreateThread failed (err=%lu); detection unavailable.", GetLastError());
        s_threadId = 0;
        if (s_queueReady) { CloseHandle(s_queueReady); s_queueReady = nullptr; }
        return;
    }

    // 等佇列就緒；等不到就放棄偵測，不讓主迴圈卡在這裡
    if (s_queueReady && WaitForSingleObject(s_queueReady, 2000) != WAIT_OBJECT_0) {
        Log(L"[CursorConflict] message queue not ready in time; detection unavailable.");
        s_threadId = 0;
        return;
    }
    Log(L"[CursorConflict] thread started.");
}

void SetEnabled(bool enabled) {
    if (!s_threadId) return;
    static int lastPosted = -1;
    const int want = enabled ? 1 : 0;
    if (want == lastPosted) return;
    if (PostThreadMessageW(s_threadId, kMsgSetEnabled, (WPARAM)want, 0))
        lastPosted = want;
    else
        Log(L"[CursorConflict] PostThreadMessage failed (err=%lu).", GetLastError());
}

void NoteStickActivity() {
    s_lastStickMs.store(GetTickCount64(), std::memory_order_relaxed);
}

bool IsExternalCursorActive() {
    const ULONGLONG last = s_lastDetectMs.load(std::memory_order_relaxed);
    if (last == 0) return false;
    return GetTickCount64() - last <= kActiveHoldMs;
}

} // namespace CursorConflict
