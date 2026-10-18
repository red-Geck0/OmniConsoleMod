#pragma once
#include <windows.h>

// ============================================================================
// CursorConflict：外部「手把轉游標」注入偵測
// ============================================================================
//
// Windows 11 的 Game Bar 內建「Gamepad Cursor」（設定 → Gamepad Cursor）會把
// 左搖桿變成滑鼠游標、右搖桿變成滾輪、A 鍵變成左鍵——與 Mouse Mode 完全重疊。
// 兩邊同時作用時游標速度加倍、捲動互相打架、A 鍵同時送出點擊與使用者的映射。
// Steam Input 桌面配置、DS4Windows、Armoury Crate 桌面模式也是同一類衝突。
//
// Game Bar 把該開關存在自己套件的 settings.dat（GUID 命名的 JSON blob，未公開
// 且未進 GA 通道的機器上根本不存在），讀設定值不可靠；改以行為偵測：
//
//   低階滑鼠掛鉤看到「被注入（LLMHF_INJECTED）、且 dwExtraInfo 不是我們自己的
//   標記」的游標/滾輪事件，而同一時間手把搖桿正被推著 → 有別人也在把搖桿轉成
//   游標。單發事件不算數，需在計數窗內累積到門檻才判定成立。
//
// 掛鉤只在 Mouse Mode 實際啟用時掛上（SetEnabled），避免無謂的系統層負擔。
// ============================================================================

namespace CursorConflict {

    // 啟動掛鉤執行緒（自帶訊息迴圈）。掛鉤本身預設未掛上，需另呼叫 SetEnabled(true)。
    void Start();

    // 掛上／取下低階滑鼠掛鉤。以 thread message 轉交掛鉤執行緒執行
    //（SetWindowsHookEx 的掛鉤必須由擁有訊息迴圈的那條執行緒安裝）。
    void SetEnabled(bool enabled);

    // 主迴圈每 tick 回報「目前有搖桿被推出死區」。掛鉤只採計此時間點附近的注入事件。
    void NoteStickActivity();

    // 是否偵測到外部游標注入（判定成立後維持一段時間，避免提示閃爍）。
    bool IsExternalCursorActive();

} // namespace CursorConflict
