#pragma once
#include <string>
#include "Config.h"

// ============================================================================
// 規則表
// ============================================================================

struct InputRule {
    const wchar_t* processName;   // 比對前景程式名（不含 .exe，大小寫不敏感）
    const wchar_t* shortCombo;    // View 短按送出的快速鍵
    const wchar_t* longCombo;     // View 長按送出的快速鍵（空字串=不觸發）
};

// 取得前景視窗的行程名（不含 .exe）
std::wstring GetForegroundProcessName();

// 從規則表中查詢與前景程式匹配的規則
const InputRule* FindRuleForForeground();

// Whitelist mode: aktif hanya jika processName ada di cfg.mouseModeWhitelist
// (+ special detection untuk explorer file browser & steamwebhelper desktop mode)
bool IsMouseModeTarget(const std::wstring& processName, const AppConfig& cfg);

// Blacklist mode: aktif untuk semua app KECUALI processName ada di cfg.mouseModeBlacklist
// (+ special detection untuk FSE Task View, Steam Big Picture, UWP frame host)
bool IsMouseModeForceExcluded(const std::wstring& processName, const AppConfig& cfg);

// 診斷：記錄前景視窗的 proc / class / title / coversMonitor / cloaked 狀態，
// 用以辨識 explorer 子類（檔案總管 vs FSE Task View）與 Steam 模式（桌面 vs Big Picture）
void LogForegroundWindowDiagnostics();
