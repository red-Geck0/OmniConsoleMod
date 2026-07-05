#include <windows.h>
#include <xinput.h>
#include <appmodel.h>
#include <algorithm>

#pragma comment(lib, "xinput.lib")

#include "Log.h"
#include "Config.h"
#include "SteamConfig.h"
#include "ForegroundMonitor.h"
#include "InputSender.h"
#include "MouseMode.h"
#include "GamepadProfiles.h"
#include "PingService.h"

// ============================================================================
// Profile 判斷小工具
// ============================================================================

// 「空映射 profile」：所有按鍵 action 皆為 None。
// 用途：當 ResolveProfileForForeground 取得一個無任何映射的 profile
//      （如內建的 "None" 或使用者手動 Clear All 的 profile）時，等同停用 Mouse Mode。
// 採行為判斷而非比對 id，名稱即使更名仍能正確識別。
static bool IsProfileEffectivelyEmpty(const GamepadProfile& p) {
    return std::all_of(p.bindings.begin(), p.bindings.end(),
        [](const Action& a) { return a.kind == ActionKind::None; });
}

// ============================================================================
// FSE 狀態查詢
// ============================================================================

typedef BOOL(WINAPI* PfnIsGamingFseActive)();
static PfnIsGamingFseActive LoadIsGamingFseActive() {
    HMODULE hMod = LoadLibraryW(L"api-ms-win-gaming-experience-l1-1-0.dll");
    if (!hMod) return nullptr;
    return reinterpret_cast<PfnIsGamingFseActive>(
        GetProcAddress(hMod, "IsGamingFullScreenExperienceActive"));
}

// ============================================================================
// 程式進入點
// ============================================================================

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int) {
    InitLog();
    Log(L"[PhantomKey] started.");

    // 單例 Mutex：同一登入工作階段同時只允許一個 PhantomKey 實例（Local\ 命名空間）
    HANDLE hMutex = CreateMutexW(NULL, TRUE, L"Local\\OmniConsole_PhantomKey");
    if (!hMutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        Log(L"[PhantomKey] Another instance already running, exiting.");
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    Log(L"[PhantomKey] Singleton acquired.");

    // 驗證 OmniConsole MSIX 套件是否已安裝
    {
        const wchar_t* familyName = L"cc4eb8d7-a694-4b39-be86-edccdf890305_1dnwtebwr9ekg";
        UINT32 count = 0, bufLen = 0;
        (void)FindPackagesByPackageFamily(familyName, PACKAGE_FILTER_HEAD, &count, NULL, &bufLen, NULL, NULL);
        if (count == 0) {
            Log(L"[PhantomKey] OmniConsole package not installed, exiting.");
            CloseHandle(hMutex);
            return 1;
        }
        Log(L"[PhantomKey] OmniConsole package verified (count=%u).", count);
    }

    // 讀取設定
    AppConfig config = ReadConfig();
    SteamOverlayConfig steamCfg = ReadSteamOverlayConfig();
    WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut); // 同步給 Widget 讀
    unsigned long long lastIniMTime = GetSharedIniLastWriteTime();
    unsigned long long lastSteamVdfMTime = GetSteamLocalConfigLastWriteTime();

    // 載入手把映射 profile store（GamepadProfiles.json，與 Shared.ini 同目錄）
    GamepadProfileStore profileStore = LoadGamepadProfileStore();
    unsigned long long lastProfilesMTime = GetGamepadProfilesLastWriteTime();
    // 把 profile id+名稱清單同步到 Shared.ini，供 PhantomLink Widget 讀取
    {
        std::vector<ProfileListEntry> profileList;
        for (const auto& p : profileStore.profiles) profileList.push_back({ p.id, p.name, p.isReadOnly });
        WriteProfileList(profileList, profileStore.defaultProfileId);
    }

    // FSE 狀態查詢函式：載入成功時主迴圈會在偵測到 FSE 退出時結束 PhantomKey；
    // 載入失敗（API 不存在）時 pfnIsFseActive 為 nullptr，主迴圈跳過該檢查、繼續執行
    auto pfnIsFseActive = LoadIsGamingFseActive();
    if (!pfnIsFseActive)
        Log(L"[PhantomKey] WARNING: Failed to load IsGamingFullScreenExperienceActive.");

    // 啟動 ping 服務（健康檢查回應通道）：建立 message-only window，主程式可透過 SendMessageTimeout 量測主迴圈推進狀況
    PingService::Start();

    Log(L"[PhantomKey] Entering main loop.");

    // 自適應輪詢狀態
    DWORD sleepMs = 100;        // 初始閒置頻率 ~10Hz
    int idleTicks = 0;

    // 按鍵偵測狀態
    LARGE_INTEGER freq, pressStart, now;
    QueryPerformanceFrequency(&freq);
    pressStart.QuadPart = 0;

    // View（⧉）按鍵狀態
    bool viewWasPressed = false;
    bool viewLongPressFired = false;

    // Menu（☰）按鍵狀態
    bool menuWasPressed = false;
    bool menuLongPressFired = false;
    LARGE_INTEGER menuPressStart;
    menuPressStart.QuadPart = 0;

    // 前景程式偵測
    std::wstring lastFgProcess;

    // 前景 HWND 快取：行程名/路徑只在前景視窗（HWND）改變時才需重查（OpenProcess + 字串配置昂貴）。
    // 同一視窗永遠屬於同一行程，故以 HWND 為鍵快取行程資訊安全無虞。
    HWND lastFgHwnd = nullptr;
    std::wstring cachedFg, cachedFgPath;

    // 身分解析逾時：OpenProcess / QueryFullProcessImageName 短暫失敗屬正常（視窗剛出現時的
    // race），故失敗時不更新 lastFgHwnd、下一 tick 重試（見下方偵測邏輯）。但若同一個新 HWND
    // 持續解析失敗超過此時限（例如受保護行程長期無法查詢），代表身分「查不到」而非「暫時查不到」；
    // 此時放棄沿用舊 HWND 的快取身分（否則會誤把已離開前景的舊 app 身分套用到新 app 上），
    // 改提交「身分不明」（procName/fullPath 皆空），交由下游走一般未指派 app 的預設路徑。
    HWND pendingFgHwnd = nullptr;
    ULONGLONG pendingSinceMs = 0;
    const ULONGLONG kIdentityUnknownTimeoutMs = 2000; // 逾時上限：2 秒

    // mtime 輪詢節流：輸入活躍時主迴圈跑 ~125Hz，但設定檔變更偵測不需這麼高頻。
    // 以時間為基準每 ~50ms 才 stat 一次三個檔案，砍掉活躍輸入時大量無謂的檔案系統查詢。
    ULONGLONG lastMtimeCheck = 0;

    // Profile 解析快取：ResolveProfileForForeground 每次都做 AUMID 解析（OpenProcess +
    // GetApplicationUserModelId）與 ApplicationFrameHost 的 EnumChildWindows，昂貴。
    // 結果僅依前景 HWND（同視窗永屬同行程 → 同 profile），故以 HWND 為鍵快取。
    // 注意：cachedProfile 指向 profileStore 內部；profileStore 重載時必須失效（見下方 reload 區塊）。
    // Sticky-upward：昂貴的 assignment 解析以 HWND 為鍵；但「非遊戲」判定不立即鎖死——
    // 未指派且暫判非遊戲（PlainDefault）時，在 10 秒觀察窗內續查 game-guess（cheap），
    // 讓 windowed→fullscreen-belakangan 的遊戲能被升級為 gameDefault。一旦判定為遊戲 /
    // 命中 assignment / 超過 10 秒 → cacheHard 鎖定，之後直接用快取不再重算。
    HWND cachedProfileHwnd = nullptr;
    const GamepadProfile* cachedProfile = nullptr;
    bool cacheHard = false;             // true = 已鎖定（assigned / game / 觀察逾時）
    ULONGLONG profileWatchStartMs = 0;  // 此 HWND 開始觀察的時刻
    const ULONGLONG kGameWatchMs = 10000; // 觀察窗上限：10 秒

    // 常駐主迴圈
    while (true) {
        Sleep(sleepMs);

        // 心跳：每圈更新一次；ping 執行緒讀此值回報主迴圈推進狀況
        PingService::UpdateHeartbeat();

        // XInput 輪詢：遍歷所有手把，收集 View/Menu 按鍵，取最後一支有顯著輸入的手把狀態
        XINPUT_GAMEPAD activePad = {};
        bool viewPressed = false;
        bool menuPressed = false;
        for (DWORD i = 0; i < 4; i++) {
            XINPUT_STATE state = {};
            if (XInputGetState(i, &state) != ERROR_SUCCESS) continue;
            const auto& g = state.Gamepad;
            if (g.wButtons & XINPUT_GAMEPAD_BACK)  viewPressed = true;
            if (g.wButtons & XINPUT_GAMEPAD_START) menuPressed = true;
            if (g.wButtons || g.bLeftTrigger || g.bRightTrigger ||
                abs(g.sThumbLX) > 8000 || abs(g.sThumbLY) > 8000 ||
                abs(g.sThumbRX) > 8000 || abs(g.sThumbRY) > 8000) {
                activePad = g;
            }
        }

        // 前景視窗 HWND（GetForegroundWindow 極輕量，回傳快取值）。
        HWND fgHwnd = GetForegroundWindow();
        ULONGLONG nowMs = GetTickCount64();

        // 前景程式變化偵測 → 重新讀取設定 + 重設 Mouse Mode 狀態 + FSE 退出檢查
        // 僅在 HWND 改變時才重查行程名/路徑（避免每 tick OpenProcess + 字串配置）。
        if (fgHwnd != lastFgHwnd) {
            std::wstring fg, fgPath;
            GetForegroundProcessInfo(fg, fgPath);
            if (!fg.empty()) {
                // 查詢成功 → 提交新身分，清除逾時追蹤狀態。
                lastFgHwnd    = fgHwnd;
                cachedFg      = fg;
                cachedFgPath  = fgPath;
                // 前景身分已更新 → profile 解析快取失效，強制以新身分重解析
                // （否則身分剛從空字串修復、但 (HWND,fullscreen) 鍵未變 → 仍回傳卡住的舊結果）
                cachedProfileHwnd = nullptr;
                cacheHard = false;
                pendingFgHwnd = nullptr;
            } else if (pendingFgHwnd != fgHwnd) {
                // 這個新 HWND 首次查詢失敗：開始計時，暫不放棄舊快取（下一 tick 重試）。
                pendingFgHwnd  = fgHwnd;
                pendingSinceMs = nowMs;
            } else if (nowMs - pendingSinceMs >= kIdentityUnknownTimeoutMs) {
                // 同一 HWND 持續查詢失敗超過逾時 → 放棄沿用舊 app 快取身分，改提交「身分不明」
                // （空字串），避免無限期誤套用已離開前景的舊 app 身分到目前這個新視窗。
                Log(L"[PhantomKey] Identity query timed out (>%dms) for new foreground window; "
                    L"treating as unknown (was [%s]).", (int)kIdentityUnknownTimeoutMs, cachedFg.c_str());
                lastFgHwnd    = fgHwnd;
                cachedFg.clear();
                cachedFgPath.clear();
                cachedProfileHwnd = nullptr;
                cacheHard = false;
                pendingFgHwnd = nullptr;
            }
            // 否則：仍在逾時窗內，維持舊快取不變，下一 tick 重試。
        }
        std::wstring& currentFg = cachedFg;
        std::wstring& currentFgPath = cachedFgPath;
        if (currentFg != lastFgProcess) {
            Log(L"[PhantomKey] FG changed: [%s] -> [%s].", lastFgProcess.c_str(), currentFg.c_str());
            LogForegroundWindowDiagnostics();
            lastFgProcess = currentFg;

            // 不在 FSE 中 → 結束 PhantomKey
            if (pfnIsFseActive && !pfnIsFseActive()) {
                Log(L"[PhantomKey] FSE no longer active, exiting.");
                break;
            }

            // 切到 steamwebhelper 時重讀 SteamConfig：涵蓋首次登入（Steam 未安裝 / 未登入時 vdf 不存在）與帳號切換
            // Overlay 快捷鍵改動的同步走下方 localconfig.vdf mtime 監看，不依賴前景切換
            if (_wcsicmp(currentFg.c_str(), L"steamwebhelper") == 0) {
                steamCfg = ReadSteamOverlayConfig();
                WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut); // 同步給 Widget 讀
                lastSteamVdfMTime = GetSteamLocalConfigLastWriteTime();
            }

            MouseMode::Reset();
        }

        // 設定檔變更偵測（mtime 輪詢）：每 ~50ms 才 stat 一次三個檔案，避免活躍輸入時 ~125Hz 的無謂檔案系統查詢。
        // 設定變更為使用者操作觸發（非即時性需求），50ms 延遲偵測對體感無影響。
        if (nowMs - lastMtimeCheck >= 50) {
            lastMtimeCheck = nowMs;

            // Shared.ini 被改寫（主程式或 PhantomLink 操作）→ 即時重載 AppConfig
            unsigned long long curIniMTime = GetSharedIniLastWriteTime();
            if (curIniMTime != 0 && curIniMTime != lastIniMTime) {
                Log(L"[PhantomKey] Shared.ini changed, reloading config.");
                lastIniMTime = curIniMTime;
                config = ReadConfig();
                MouseMode::Reset();
            }

            // GamepadProfiles.json 被改寫（主程式編輯器存檔）→ 即時重載 profile
            unsigned long long curProfilesMTime = GetGamepadProfilesLastWriteTime();
            if (curProfilesMTime != lastProfilesMTime) {
                Log(L"[PhantomKey] GamepadProfiles.json changed, reloading profiles.");
                GamepadProfileStore newStore = LoadGamepadProfileStore();
                // 防止「檔案寫到一半」被讀到 → 解析失敗回空 store 會清掉所有 profile 造成
                // 一瞬間 mapping 失效。store 永遠至少含內建 profile，故 empty 必為讀取失敗：
                // 此時保留舊 store 且不更新 mtime → 下一 tick 重試（待寫入完成）。
                if (!newStore.profiles.empty()) {
                    lastProfilesMTime = curProfilesMTime;
                    profileStore = std::move(newStore);
                    // profileStore 重新配置 → 舊的 cachedProfile 指標失效，清除快取
                    cachedProfileHwnd = nullptr;
                    cachedProfile = nullptr;
                    cacheHard = false;
                    {
                        std::vector<ProfileListEntry> profileList;
                        for (const auto& p : profileStore.profiles) profileList.push_back({ p.id, p.name, p.isReadOnly });
                        WriteProfileList(profileList, profileStore.defaultProfileId);
                    }
                    MouseMode::Reset();
                }
            }

            // localconfig.vdf 被改寫（使用者在 SteamBigPicture 調整 overlay 快捷鍵或開關）→ 即時重讀 SteamConfig
            // 涵蓋「SteamBigPicture 改快捷鍵 → 直接啟動遊戲、未回 SteamBigPicture」這條路徑（前景切換偵測點抓不到）
            // Steam 未安裝 / 未登入 / 路徑尚未確立 → GetSteamLocalConfigLastWriteTime() 回 0
            unsigned long long curSteamVdfMTime = GetSteamLocalConfigLastWriteTime();
            if (curSteamVdfMTime != 0 && curSteamVdfMTime != lastSteamVdfMTime) {
                Log(L"[PhantomKey] localconfig.vdf changed, reloading SteamConfig.");
                lastSteamVdfMTime = curSteamVdfMTime;
                steamCfg = ReadSteamOverlayConfig();
                WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut);
            }
        }

        // Mouse Mode 決策順序（每 tick）：
        //   1. MouseMode 關閉 / 裝置內建廠商映射 → 不介入
        //   2. Widget 目前浮現（WidgetActive=1） → 不介入，讓 Game Bar 原生手把 UI 正常運作
        //   3. IsMouseModeForceExcluded(P)       → 不介入（系統黑名單 Tier-1：
        //        OmniConsole 自己 / Playnite / SteamBigPicture / Xbox / Armoury /
        //        Windows 設定 / Microsoft Store / FSE Task View）
        //   4. 否則 → 解析前景應套用的 profile（assignment 命中則用其 profile，
        //             否則用 defaultProfileId 的 profile）→ 啟用，走 Tick；
        //             同時將 activeProfileId 寫入 Shared.ini 供 Widget 預選
        bool mouseModeActive = false;
        const GamepadProfile* activeProfile = nullptr;
        if (config.mouseModeEnabled &&
            !config.hasBuiltInGamepadMapping &&
            !config.widgetActive &&
            !IsMouseModeForceExcluded(currentFg)) {
            // Sticky-upward 快取：cacheHard 時直接用快取（assigned/game/逾時已鎖定）；
            // 否則重解析。PlainDefault（未指派非遊戲）暫不鎖定，於 10 秒觀察窗內續查，
            // 讓 windowed→fullscreen-belakangan 的遊戲能升級為 gameDefault。
            if (fgHwnd == cachedProfileHwnd && cacheHard) {
                activeProfile = cachedProfile;
            } else {
                ResolveOutcome oc = ResolveOutcome::PlainDefault;
                activeProfile = ResolveProfileForForeground(profileStore, currentFg, currentFgPath, fgHwnd, &oc);
                // AFH 宿主 AUMID 尚未就緒（Provisional）→ 不快取，下一 tick 重試。
                if (oc != ResolveOutcome::Provisional) {
                    if (fgHwnd != cachedProfileHwnd) {
                        cachedProfileHwnd = fgHwnd;
                        profileWatchStartMs = nowMs;   // 新視窗 → 重置觀察窗
                    }
                    cachedProfile = activeProfile;
                    // 鎖定條件：命中 assignment / 已判定遊戲 / 觀察逾時（10 秒仍非遊戲）。
                    bool gameOrAssigned = (oc == ResolveOutcome::Assigned ||
                                           oc == ResolveOutcome::GameDefault);
                    bool watchExpired = (nowMs - profileWatchStartMs) >= kGameWatchMs;
                    cacheHard = gameOrAssigned || watchExpired;
                }
            }
            if (activeProfile) {
                // 一律回報已解析的 profile id（含空映射的 "None"），讓 Widget 能正確預選——
                // 否則指派 None 的 app 不會更新 ActiveProfileId，Widget 會顯示舊 profile。
                // Mouse Mode 仍只在非空 profile 時才真正啟用。
                WriteActiveProfileId(activeProfile->id);
                if (!IsProfileEffectivelyEmpty(*activeProfile))
                    mouseModeActive = true;
            }
        }

        // 自適應輪詢頻率
        if (viewPressed || viewWasPressed || menuPressed || menuWasPressed || mouseModeActive) {
            sleepMs = 8;        // 輸入偵測中 ~125Hz
            idleTicks = 0;
        } else {
            idleTicks++;
            if (idleTicks > 30) sleepMs = 100;  // 恢復閒置 ~10Hz
        }

        // ── View（⧉）狀態機：短按 / 長按 ──
        if (viewPressed && !viewWasPressed) {
            QueryPerformanceCounter(&pressStart);
            viewLongPressFired = false;
        } else if (viewPressed && viewWasPressed && !viewLongPressFired) {
            QueryPerformanceCounter(&now);
            double holdMs = (double)(now.QuadPart - pressStart.QuadPart) / freq.QuadPart * 1000.0;

            if (holdMs > 500.0) {
                const InputRule* rule = FindRuleForForeground();
                if (rule && rule->longCombo[0] != L'\0') {
                    Log(L"[PhantomKey] View long press (%dms). FG matched [%s]. Sending: %s",
                        (int)holdMs, rule->processName, rule->longCombo);
                    SendKeyCombo(rule->longCombo);
                }
                viewLongPressFired = true;
            }
        } else if (!viewPressed && viewWasPressed) {
            if (!viewLongPressFired) {
                const InputRule* rule = FindRuleForForeground();
                if (rule) {
                    Log(L"[PhantomKey] View short press. FG matched [%s]. Sending: %s",
                        rule->processName, rule->shortCombo);
                    SendKeyCombo(rule->shortCombo);
                }
            }
            viewLongPressFired = false;
        }
        viewWasPressed = viewPressed;

        // ── Menu（☰）狀態機：長按 → Steam In-Game Overlay ──
        if (menuPressed && !menuWasPressed) {
            QueryPerformanceCounter(&menuPressStart);
            menuLongPressFired = false;
        } else if (menuPressed && menuWasPressed && !menuLongPressFired) {
            QueryPerformanceCounter(&now);
            double holdMs = (double)(now.QuadPart - menuPressStart.QuadPart) / freq.QuadPart * 1000.0;

            if (holdMs > 500.0) {
                bool shouldFire =
                    _wcsicmp(config.defaultPlatform.c_str(), L"SteamBigPicture") == 0 &&
                    config.steamOverlayEnabled &&
                    steamCfg.overlayEnabled &&
                    _wcsicmp(currentFg.c_str(), L"steamwebhelper") != 0 &&
                    _wcsicmp(currentFg.c_str(), L"explorer") != 0;

                if (shouldFire) {
                    Log(L"[PhantomKey] Menu long press (%dms). FG=[%s]. Sending overlay: %s",
                        (int)holdMs, currentFg.c_str(), steamCfg.overlayShortcut.c_str());
                    SendKeyCombo(steamCfg.overlayShortcut);
                }
                menuLongPressFired = true;
            }
        }
        menuWasPressed = menuPressed;

        // ── Mouse Mode：前景為目標程式時將手把映射為滑鼠+鍵盤 ──
        // 檔案總管對 D-pad 已有原生反應，跳過 D-pad 映射避免雙跳；其他鍵仍由 Mouse Mode 處理
        if (mouseModeActive && activeProfile) {
            bool skipDpad = ForegroundHandlesDpadNatively(currentFg);
            MouseMode::Tick(activePad, *activeProfile, skipDpad);
        }
    }

    // 清理資源（FSE 退出後 break 到此）
    ReleaseMutex(hMutex);
    CloseHandle(hMutex);
    Log(L"[PhantomKey] ended.");
    return 0;
}
