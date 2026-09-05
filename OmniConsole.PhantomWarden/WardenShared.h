#pragma once

// ============================================================================
// PhantomWarden 與主程式共用的固定名稱
// ============================================================================
//
// C# 端的對應常數在 OmniConsole/Services/ElevatedInputService.cs，
// 兩邊必須一致；改動時務必同步。
// ============================================================================

// %ProgramData% 底下的安裝目錄名稱（受保護 DACL，一般權限只能讀取與執行）
constexpr const wchar_t* kWardenInstallFolderName = L"OmniConsoleMod";

// 安裝目錄內的 PhantomKey 副本檔名（與套件內同名，行程名維持 "Steam"）
constexpr const wchar_t* kWardenPayloadExeName = L"Steam.exe";

// 排程工作的資料夾與名稱（完整路徑 \OmniConsoleMod\PhantomKeyElevated）
constexpr const wchar_t* kWardenTaskFolder = L"OmniConsoleMod";
constexpr const wchar_t* kWardenTaskName   = L"PhantomKeyElevated";
