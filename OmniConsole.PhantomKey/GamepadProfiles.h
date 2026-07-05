#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include "Bindings.h"

// ============================================================================
// 手把映射 profile store
// ============================================================================
//
// GamepadProfiles.json 位於 %LOCALAPPDATA%\Publishers\<PublisherHash>\OmniConsoleShared\
//（與 Shared.ini 同目錄）。C++ 端僅讀，由 C# 端 GamepadProfileStore 寫。
//
// 模型：profile 為可命名、可重用的實體；App 透過 assignment 指派到某 profile。
//       未被指派的 App 一律套用 defaultProfileId 指向的 profile。
//
// 前景解析流程：先試取前景 AUMID（ApplicationFrameHost 宿主走 CoreWindow 反查宿主
//   pid，自跑 exe 的 packaged 直接對前景 pid 取）。
//   - 取到 AUMID → 只比 kind=Aumid 的 assignment，未命中不回退 process 名稱。
//   - 取不到（Win32 桌面 process）→ procName + fullPath 雙件相符的 assignment 才命中。
//   命中 assignment → 用其 profile；未命中 → 用 defaultProfileId 的 profile。
//
// 與 C# 端 OmniConsole/Services/GamepadProfileStore.cs 對應。
// ============================================================================

// ── App 識別（assignment 用） ───────────────────────────────────────────────
struct AppId {
    enum class Kind { Process, Aumid };
    Kind         kind  = Kind::Process;
    std::wstring value;
    std::wstring fullPath;  // 僅 Kind=Process 適用；空字串代表 name 通配
};

// ── Layered Mode 設定 ───────────────────────────────────────────────────────
enum class LayeredActivationMode { HoldRelease, DoubleTapToggle };

struct ProfileLayered {
    bool                  enabled        = false;
    KeyId                 triggerKey     = KeyId::RS;
    LayeredActivationMode activationMode = LayeredActivationMode::HoldRelease;
};

// ── 一份 profile ────────────────────────────────────────────────────────────
struct GamepadProfile {
    std::wstring   id;
    std::wstring   name;
    bool           isBuiltIn          = false;
    bool           isReadOnly         = false;
    int            cursorSpeedPercent = 100;
    bool           dpadAutoRepeat     = true;   // D-pad 補 keydown（導覽用）或純鏡像按住（遊戲用）
    ProfileLayered layered;
    Bindings       bindings{};
};

// ── App → profile 指派 ──────────────────────────────────────────────────────
struct ProfileAssignment {
    AppId        appId;
    std::wstring profileId;
};

// ── 整個 store ──────────────────────────────────────────────────────────────
struct GamepadProfileStore {
    std::wstring                   defaultProfileId;      // 未指派 + 前景非遊戲 → 套此
    std::wstring                   gameDefaultProfileId;  // 未指派 + 前景是遊戲 → 套此
    std::vector<GamepadProfile>    profiles;
    std::vector<ProfileAssignment> assignments;
};

// ── 讀取與解析 ──────────────────────────────────────────────────────────────

// 從 GamepadProfiles.json 讀取整個 store；檔案不存在或解析失敗回空 store
GamepadProfileStore LoadGamepadProfileStore();

// 回傳 GamepadProfiles.json 的最後寫入時間（FILETIME 壓成 uint64_t）；不存在回 0
unsigned long long GetGamepadProfilesLastWriteTime();

// 取前景視窗的 AUMID：先試視窗屬性存放區（AFH 宿主 UWP），空則回退以前景行程直接取
// （涵蓋自跑 exe 的 packaged app，例 Shift Game Launcher packaged 版 / Microsoft Store）。
// 非 packaged 桌面 process 回空字串。
std::wstring GetForegroundAumid(HWND hwnd);

// 前景解析結果類型（供呼叫端決定是否「鎖定」快取）：
//   Provisional  — 身分尚未就緒（AFH 宿主 AUMID 未解析）→ 不快取，重試。
//   Assigned     — 命中 assignment → 可立即鎖定。
//   GameDefault  — 未指派但判定為遊戲 → 可立即鎖定（sticky）。
//   PlainDefault — 未指派且非遊戲 → 暫不鎖定；呼叫端可在限定時間內續查 game-guess。
//   NoProfile    — 連 default 都找不到。
enum class ResolveOutcome { Provisional, Assigned, GameDefault, PlainDefault, NoProfile };

// 解析前景 App 應套用的 profile：先查 assignment；未命中時依前景是否為遊戲
// 回 gameDefaultProfileId 或 defaultProfileId 的 profile。
// store 內無對應 profile（含 default 都找不到）時回 nullptr。
// outcomeOut 非 null 時回傳上述結果類型（含 Provisional）。
const GamepadProfile* ResolveProfileForForeground(const GamepadProfileStore& store,
                                                  const std::wstring& procName,
                                                  const std::wstring& fullPath,
                                                  HWND fgHwnd,
                                                  ResolveOutcome* outcomeOut = nullptr);

// 判定前景 App 是否「可能是遊戲」，兩個訊號擇一命中即視為遊戲：
//   1. 前景 exe 登記於 HKCU\System\GameConfigStore\Children（Windows GameDVR / Game Bar
//      記錄的遊戲）— 以 fullPath 為鍵靜態快取，僅路徑改變時掃 registry。
//   2. 前景視窗為 borderless / exclusive 全螢幕（視窗 rect 覆蓋整個 rcMonitor）—
//      涵蓋現代無邊框全螢幕遊戲；排除 Shell（桌面 / 工作列）。
// 純讀 registry + user32 查詢，無注入、無 hook。
// 註：全螢幕為「首次認定」時的訊號之一；認定後不再因全螢幕切換而重算（呼叫端以 HWND 鎖定快取）。
// procName 用於排除已知非遊戲的啟動器/客戶端行程（例如桌面 Steam 的 steamwebhelper.exe 在手把裝置上
// 常見以填滿螢幕的視窗執行，若不排除會被訊號 2 誤判成遊戲、自動套用 Gaming profile）。
bool IsForegroundLikelyGame(const std::wstring& procName, const std::wstring& fullPath, HWND fgHwnd);
