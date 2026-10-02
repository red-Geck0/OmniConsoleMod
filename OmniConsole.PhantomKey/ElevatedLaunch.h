#pragma once
#include <windows.h>
#include <string>

// ============================================================================
// 免 UAC 提權啟動應用程式（Route B）
// ============================================================================
//
// OmniConsole 主程式（一般權限）無法讓目標以系統管理員身分啟動而不跳 UAC：
//   - 封裝 App（MSIX）走 AppModel 啟動，完整性等級由系統決定，指定不了。
//   - 一般 exe 得用 runas 動詞提權，那必定跳一次 UAC。
// 改由已提權常駐的 PhantomKey 代打——它跑在 High IL，直接 CreateProcess 目標 exe，
// 子行程即繼承 High IL（封裝 App 另因 exe 位在 WindowsApps 而仍取得 package identity）。
//
// 目標 exe 路徑（與一般 exe 的參數）由主程式當場解析後寫進 Shared.ini [Launch]，
// PhantomKey 這端只負責在 High IL 下啟動它。
//
// 安全防線：Shared.ini 位於使用者可寫的 LocalAppData，任何一般權限行程都能改寫它。
// 因此提權啟動前一律驗證目標 exe 落在「僅系統管理員可寫」的位置
//（%ProgramFiles% / %ProgramFiles(x86)% / %SystemRoot%，WindowsApps 亦在其下），
// 那些地方一般權限程式（含惡意程式）改不動檔案，代打啟動它才不會變成提權後門。
// 位在使用者可寫資料夾（如 C:\Games、LocalAppData）的 exe 一律拒絕——主程式那端會
// 對這種目標改走 runas（跳 UAC），不會送到這裡。

// 以目前行程的完整性等級啟動 exePath（args 可為空）。
// 成功回子行程 PID；目標不在「僅系統管理員可寫」位置、或啟動失敗時回 0。
// 主程式（OmniConsole）用這個 PID 偵測平台視窗是否已出現（即使還在自己視窗後方），
// 好在對的時機把自己藏起來讓平台自然浮到前景（見 LaunchPage / Config WriteLaunchPid）。
DWORD LaunchElevated(const std::wstring& exePath, const std::wstring& args);
