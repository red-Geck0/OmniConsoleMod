#pragma once
#include <string>
#include <vector>

// ============================================================================
// 共用設定讀取（PublisherCacheFolder\OmniConsoleShared\Shared.ini）
// ============================================================================

// Whitelist = aktif hanya untuk app di daftar [MouseMode.Whitelist] (dulu Auto)
// Blacklist  = aktif untuk semua app KECUALI daftar [MouseMode.Blacklist] (dulu ForceOn)
// OmniList  = hybrid: whitelist apps = no layered, non-whitelist = layered ON, blacklist = disabled
enum class MouseModeState { Off, Whitelist, Blacklist, OmniList };

// Index ke array mapping (sinkron dengan kButtonNames di Config.cpp).
enum ButtonIdx {
    BTN_A = 0, BTN_B, BTN_X, BTN_Y,
    BTN_LB, BTN_RB, BTN_LT, BTN_RT,
    BTN_LSPress, BTN_RSPress,
    BTN_DPadUp, BTN_DPadDown, BTN_DPadLeft, BTN_DPadRight,
    BTN_COUNT
};

struct AppConfig {
    std::wstring   defaultPlatform;           // [General] DefaultPlatform
    bool           steamOverlayEnabled;       // [PhantomKey] SteamInGameOverlayEnabled
    MouseModeState mouseMode;                 // [PhantomKey] MouseMode，預設 Whitelist
    std::wstring   mouseModeLayout;           // [PhantomKey] MouseModeLayout，"OmniNav"|"Classic"，預設 "OmniNav"
    int            cursorSpeedPercent;        // [PhantomKey] CursorSpeedPercent，25/50/75/100/125/150/175/200，預設 100
    bool           hasBuiltInGamepadMapping;  // 讀取時獨立偵測 BIOS SystemProductName（ROG Ally 家族等）

    // ── App lists untuk Whitelist / Blacklist mode ────────────────────────────
    // Dibaca dari [MouseMode.Whitelist] Apps= dan [MouseMode.Blacklist] Apps= (CSV)
    // explorer & steamwebhelper TIDAK di sini — keduanya pakai special window detection di C++
    std::vector<std::wstring> mouseModeWhitelist;  // [MouseMode.Whitelist] Apps=
    std::vector<std::wstring> mouseModeBlacklist;  // [MouseMode.Blacklist] Apps=

    // ── Button Mapping per layout ────────────────────────────────────────────
    // 14 string mapping per layout, dibaca dari [Mapping.OmniNav] / [Mapping.Classic].
    // Format: "modifier+modifier+key" atau token khusus (lclick/rclick/wheelup/dll).
    std::wstring mapOmniNav[BTN_COUNT];
    std::wstring mapClassic[BTN_COUNT];

    // ── Layered Mode per layout ───────────────────────────────────────────────
    // [LayeredMode.OmniNav] / [LayeredMode.Classic]
    bool         layeredEnabledOmniNav;
    int          layeredButtonOmniNav;        // ButtonIdx
    bool         layeredEnabledClassic;
    int          layeredButtonClassic;        // ButtonIdx
};

// String "A".."DPadRight" untuk diakses Config / MouseMode.
extern const wchar_t* const kButtonNames[BTN_COUNT];

// Konversi nama → index. Return -1 jika tidak dikenali.
int ButtonNameToIdx(const std::wstring& name);

AppConfig ReadConfig();

// 回傳 Shared.ini 的最後寫入時間（FILETIME 壓成 uint64_t）；檔案不存在回 0
unsigned long long GetSharedIniLastWriteTime();

// 將 Steam In-Game Overlay 快捷鍵寫入 Shared.ini [PhantomKey] SteamInGameOverlayShortcut；
// 內部以靜態快取比對，僅在值改變時實際寫檔，避免無謂 I/O 與 mtime 變動。
// 用途：PhantomLink Widget 透過 PhantomKeyStore 讀取此鍵，傳給 PhantomBridge 觸發 overlay。
void WriteSteamInGameOverlayShortcut(const std::wstring& shortcut);

// 將目前前景程式名寫入 Shared.ini [PhantomKey] ForegroundProcess；
// 內部以靜態快取比對，僅在值改變時實際寫檔。
// 用途：PhantomLink Widget 讀取此鍵以顯示「加入/移除白名單/黑名單」按鈕。
void WriteForegroundProcess(const std::wstring& processName);
