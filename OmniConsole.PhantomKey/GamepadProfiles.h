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
    std::wstring                   defaultProfileId;
    std::vector<GamepadProfile>    profiles;
    std::vector<ProfileAssignment> assignments;
};

// ── 讀取與解析 ──────────────────────────────────────────────────────────────

// 從 GamepadProfiles.json 讀取整個 store；檔案不存在或解析失敗回空 store
GamepadProfileStore LoadGamepadProfileStore();

// 回傳 GamepadProfiles.json 的最後寫入時間（FILETIME 壓成 uint64_t）；不存在回 0
unsigned long long GetGamepadProfilesLastWriteTime();

// 取前景視窗的 AUMID — 只對 ApplicationFrameHost 宿主的 UWP 有效；
// 自跑 exe 的 packaged（Notepad / SnippingTool 等）回空字串
std::wstring GetForegroundAumid(HWND hwnd);

// 解析前景 App 應套用的 profile：先查 assignment，未命中回 defaultProfileId 的 profile。
// store 內無對應 profile（含 default 都找不到）時回 nullptr。
const GamepadProfile* ResolveProfileForForeground(const GamepadProfileStore& store,
                                                  const std::wstring& procName,
                                                  const std::wstring& fullPath,
                                                  HWND fgHwnd);
