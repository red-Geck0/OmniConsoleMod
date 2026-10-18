#pragma once
#include <string>
#include <vector>
#include <utility>

// ============================================================================
// 共用設定讀取（PublisherCacheFolder\OmniConsoleShared\Shared.ini）
// ============================================================================

struct AppConfig {
    std::wstring defaultPlatform;          // [General] DefaultPlatform
    bool         steamOverlayEnabled;      // [PhantomKey] SteamInGameOverlayEnabled
    bool         mouseModeEnabled;         // [PhantomKey] MouseMode = On/Off，預設 On
    bool         hasBuiltInGamepadMapping; // 讀取時獨立偵測 BIOS SystemProductName（ROG Ally 家族等）
    bool         widgetActive;             // [Status] WidgetActive：Widget 浮現時由 PhantomLink 寫 1
};

AppConfig ReadConfig();

// 回傳 Shared.ini 的最後寫入時間（FILETIME 壓成 uint64_t）；檔案不存在回 0
unsigned long long GetSharedIniLastWriteTime();

// 將 Steam In-Game Overlay 快捷鍵寫入 Shared.ini [PhantomKey] SteamInGameOverlayShortcut；
// 內部以靜態快取比對，僅在值改變時實際寫檔，避免無謂 I/O 與 mtime 變動。
// 用途：PhantomLink Widget 透過 PhantomKeyStore 讀取此鍵，傳給 PhantomBridge 觸發 overlay。
void WriteSteamInGameOverlayShortcut(const std::wstring& shortcut);

// 供 WriteProfileList 使用的單筆 profile 摘要（id / 顯示名稱 / 是否唯讀）。
struct ProfileListEntry {
    std::wstring id;
    std::wstring name;
    bool         isReadOnly = false;
};

// 將手把映射 profile 清單及預設 profile id 寫入 Shared.ini [Profiles]
//（Count / IdN / NameN / ReadOnlyN / DefaultId）。
// 內部以靜態快取比對，僅在清單改變時實際寫檔。
// 用途：PhantomLink Widget 透過 PhantomKeyStore 讀取此區段以填 profile 下拉選單、預選預設項，
// 並依 ReadOnlyN 決定「編輯」按鈕是否可用（唯讀 profile 開編輯器會落到無焦點可用元件的畫面）。
void WriteProfileList(const std::vector<ProfileListEntry>& profiles,
                      const std::wstring& defaultProfileId);

// 將目前套用的 profile id 寫入 Shared.ini [Status] ActiveProfileId。
// 僅在 widget 未顯示時寫入，以保留「上次使用的遊戲 profile」供 Widget 預選。
void WriteActiveProfileId(const std::wstring& profileId);

// 將「偵測到外部手把轉游標注入」旗標寫入 Shared.ini [Status] ExternalCursorConflict（1/0）。
// 內部以靜態快取比對，僅在狀態改變時實際寫檔。
// 用途：主程式 OmniNav 頁讀取此鍵，於 Mouse Mode 開關旁顯示與 Game Bar Gamepad Cursor
//（或 Steam Input / DS4Windows 等）衝突的黃色提示。
void WriteExternalCursorConflict(bool detected);

// 讀取 Shared.ini [Status] StopRequested。主程式無法直接終止以系統管理員權限
// 執行的 PhantomKey（一般權限開不了高完整性等級行程的 handle），改以此旗標
// 請它自己收工；PhantomKey 在主迴圈偵測到即結束。
bool ReadStopRequested();

// 將 StopRequested 歸零。啟動時呼叫，避免沿用上一輪留下的舊值。
void ClearStopRequested();

// 將「前景是系統管理員程式、而 PhantomKey 沒有提權，映射送不進去」旗標寫入
// Shared.ini [Status] ElevatedInputBlocked（1/0）。
// 內部以靜態快取比對，僅在狀態改變時實際寫檔。
void WriteElevatedInputBlocked(bool blocked);

// ── 免 UAC 提權啟動請求（Route B；[Launch] 區段） ────────────────────────────
//
// 主程式（一般權限）想讓封裝 App 以系統管理員身分啟動時，把當場解析好的目標 exe 路徑
// 寫進 Shared.ini，交給已提權常駐的 PhantomKey 代打（見 ElevatedLaunch.h）。
//
// 去重：主程式每次寫入帶一個唯一 Seq；PhantomKey 處理後把 Seq 回寫成 AckSeq。
// 只有 Seq 非空且與 AckSeq 不同時才啟動一次，據此避免 Shared.ini 每次變動或 PhantomKey
// 重啟時重複啟動同一請求。
struct LaunchRequest {
    std::wstring seq;   // [Launch] Seq：本次請求的唯一識別（空 = 無請求）
    std::wstring exe;   // [Launch] Exe：主程式解析出的目標 exe 絕對路徑
    std::wstring args;  // [Launch] Args：傳給 exe 的命令列參數（可空；封裝 App 通常無）
};

// 讀取目前的提權啟動請求（[Launch] Seq / Exe）。
LaunchRequest ReadLaunchRequest();

// 讀取已處理的請求識別（[Launch] AckSeq）；無則回空字串。
std::wstring ReadLaunchAck();

// 回寫已處理的請求識別（[Launch] AckSeq = seq）。
void WriteLaunchAck(const std::wstring& seq);

// 回寫本次啟動的子行程 PID（[Launch] Pid；啟動失敗回 0）。
// 主程式據此偵測平台視窗是否已出現，再把自己藏起來讓平台浮到前景。
void WriteLaunchPid(unsigned long pid);
