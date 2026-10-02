#include "ElevatedLaunch.h"
#include "Log.h"
#include <windows.h>
#include <shlobj.h>
#include <string>
#include <vector>

// 大小寫不敏感的前綴比對。
static bool StartsWithNoCase(const std::wstring& s, const std::wstring& prefix) {
    if (prefix.empty() || s.size() < prefix.size()) return false;
    return CompareStringOrdinal(s.c_str(), (int)prefix.size(),
                                prefix.c_str(), (int)prefix.size(),
                                TRUE) == CSTR_EQUAL;
}

// 取某個 CSIDL 資料夾的絕對路徑，結尾補上反斜線（供前綴比對用）。
static std::wstring FolderWithSep(int csidl) {
    wchar_t buf[MAX_PATH] = {};
    if (SUCCEEDED(SHGetFolderPathW(nullptr, csidl, nullptr, 0, buf))) {
        std::wstring s(buf);
        if (!s.empty() && s.back() != L'\\') s += L'\\';
        return s;
    }
    return std::wstring();
}

// 目標是否落在「僅系統管理員可寫」的位置。
// 用固定的受保護根目錄允許清單（Program Files / Program Files (x86) / Windows）做前綴比對，
// C# 端（ElevatedLaunchService.IsAdminOnlyLocation）採完全相同的清單與語意，兩邊必須一致：
// 主程式據此決定 delegate（免 UAC）或 runas（跳 UAC），這裡再獨立驗證一次當作硬性關卡。
// 註：WindowsApps 位於 Program Files 之下，封裝 App 亦一併涵蓋。
static bool IsAdminOnlyLocation(const std::wstring& exePath) {
    // 每個行程只解析一次這三個根目錄。
    static const std::vector<std::wstring> roots = [] {
        std::vector<std::wstring> v;
        for (int csidl : { CSIDL_PROGRAM_FILES, CSIDL_PROGRAM_FILESX86, CSIDL_WINDOWS }) {
            std::wstring r = FolderWithSep(csidl);
            if (!r.empty()) v.push_back(r);
        }
        return v;
    }();

    for (const auto& r : roots)
        if (StartsWithNoCase(exePath, r)) return true;
    return false;
}

// ── 前景協助 ────────────────────────────────────────────────────────────────
//
// 前景交接刻意不在這裡做。由排程工作啟動的 PhantomKey 沒有前景權，硬把子視窗抬到最前
// （SetForegroundWindow / BringWindowToTop）在「開機影片與平台同時載入」（Async）模式下會
// 搶在影片還在播時就把平台視窗蓋到影片上（症狀：看不到影片、只聽到聲音）。改由主程式
// OmniConsole 用回報的 PID 偵測平台視窗是否已出現，等影片播完該收尾時才把自己藏起來，
// 讓 Windows 自然把 Z-order 次位的平台視窗提升為前景——時序正確且不必爭前景權。

DWORD LaunchElevated(const std::wstring& exePath, const std::wstring& args) {
    if (exePath.empty()) {
        Log(L"[ElevatedLaunch] Empty exe path, ignoring.");
        return 0;
    }

    // 安全防線：只啟動「僅系統管理員可寫」位置的目標（見標頭說明）。
    if (!IsAdminOnlyLocation(exePath)) {
        Log(L"[ElevatedLaunch] REFUSED: target not in an admin-only location: %s", exePath.c_str());
        return 0;
    }
    if (GetFileAttributesW(exePath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Log(L"[ElevatedLaunch] Target missing: %s", exePath.c_str());
        return 0;
    }

    // 工作目錄設為 exe 所在資料夾，與正常啟動時的相對路徑解析一致。
    std::wstring workDir = exePath.substr(0, exePath.find_last_of(L'\\'));

    // CreateProcessW 繼承本行程權杖 → 子行程沿用 PhantomKey 目前的完整性等級（提權時為 High IL）。
    // 直接建立行程（非 AppModel 啟動），封裝 App 的子行程仍因 exe 位在 WindowsApps 而取得 package identity。
    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};

    // lpCommandLine 需可寫；組出 "exe" [args] 一份可寫緩衝區。
    std::wstring cmd = L"\"" + exePath + L"\"";
    if (!args.empty()) { cmd += L' '; cmd += args; }
    std::vector<wchar_t> cmdBuf(cmd.begin(), cmd.end());
    cmdBuf.push_back(L'\0');

    BOOL ok = CreateProcessW(
        exePath.c_str(),      // lpApplicationName：明確指定 exe，不靠命令列解析
        cmdBuf.data(),        // lpCommandLine：argv[0] + 參數
        nullptr, nullptr, FALSE,
        0, nullptr,
        workDir.c_str(),
        &si, &pi);

    if (!ok) {
        Log(L"[ElevatedLaunch] CreateProcess failed (err=%lu): %s", GetLastError(), exePath.c_str());
        return 0;
    }

    Log(L"[ElevatedLaunch] Launched elevated (pid=%lu): %s", pi.dwProcessId, exePath.c_str());

    // 前景交接交給主程式（見檔首說明），這裡不主動抬視窗，以免在 Async 開機影片播放中把平台
    // 蓋到影片上。只回報 PID 供主程式偵測。
    DWORD childPid = pi.dwProcessId;

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return childPid;
}
