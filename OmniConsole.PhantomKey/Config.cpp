#include "Config.h"
#include "Log.h"
#include <shlobj.h>

// ============================================================================
// 共用 INI 設定讀取（PublisherCacheFolder\OmniConsoleShared\Shared.ini）
// ============================================================================
//
// 主程式 OmniConsole 與 PhantomLink 共同寫入 PublisherCacheFolder，
// 實體路徑為 %LOCALAPPDATA%\Publishers\<PublisherHash>\OmniConsoleShared\。
// PhantomKey 透過列舉 Publishers 下的子目錄找到共用 INI。
// ============================================================================

static const wchar_t* kSharedFolderName = L"OmniConsoleShared";
static const wchar_t* kSharedIniFileName = L"Shared.ini";

// ── 解析共用 INI 路徑 ─────────────────────────────────────────────────────

static std::wstring FindSharedIniPath() {
    wchar_t localAppData[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData)))
        return L"";

    std::wstring pubBase = std::wstring(localAppData) + L"\\Publishers";
    std::wstring pattern = pubBase + L"\\*";

    WIN32_FIND_DATAW fd = {};
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return L"";

    std::wstring result;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;

        std::wstring candidate = pubBase + L"\\" + fd.cFileName + L"\\"
                                 + kSharedFolderName + L"\\" + kSharedIniFileName;
        DWORD attrs = GetFileAttributesW(candidate.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY)) {
            result = candidate;
            break;
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return result;
}

static std::wstring GetSharedIniPath() {
    static std::wstring cached;
    if (cached.empty()) cached = FindSharedIniPath();
    return cached;
}

unsigned long long GetSharedIniLastWriteTime() {
    auto path = GetSharedIniPath();
    if (path.empty()) return 0;
    WIN32_FILE_ATTRIBUTE_DATA attr = {};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) return 0;
    return ((unsigned long long)attr.ftLastWriteTime.dwHighDateTime << 32)
         | attr.ftLastWriteTime.dwLowDateTime;
}

// ── Steam In-Game Overlay 快捷鍵寫入 ───────────────────────────────────────
//
// 靜態快取比對：相同值不寫檔，避免每次主迴圈重新載入 SteamConfig 時觸發 mtime 變動
// （mtime 改變會讓 Widget 等讀取端誤以為設定有變、重新讀全部鍵值）。
void WriteSteamInGameOverlayShortcut(const std::wstring& shortcut) {
    auto path = GetSharedIniPath();
    if (path.empty()) return;
    static std::wstring lastWritten;
    if (shortcut == lastWritten) return;
    if (WritePrivateProfileStringW(L"PhantomKey", L"SteamInGameOverlayShortcut",
                                   shortcut.c_str(), path.c_str())) {
        lastWritten = shortcut;
        Log(L"[Config] Wrote SteamInGameOverlayShortcut=\"%s\" to Shared.ini", shortcut.c_str());
    }
}

// ── 手把映射 profile 清單寫入 ──────────────────────────────────────────────
//
// 靜態快取比對：清單未變則不寫，避免無謂 I/O 與 mtime 變動。
void WriteProfileList(const std::vector<ProfileListEntry>& profiles,
                      const std::wstring& defaultProfileId) {
    auto path = GetSharedIniPath();
    if (path.empty()) return;

    static std::wstring lastSig;
    std::wstring sig;
    for (const auto& p : profiles) {
        sig += p.id;   sig += L'\x01';
        sig += p.name; sig += L'\x02';
        sig += (p.isReadOnly ? L'1' : L'0'); sig += L'\x04';
    }
    sig += L'\x03';
    sig += defaultProfileId;
    if (sig == lastSig) return;
    lastSig = sig;

    // 先清整個 [Profiles] section（清掉上次殘留的 IdN/NameN/ReadOnlyN），再重寫
    WritePrivateProfileStringW(L"Profiles", nullptr, nullptr, path.c_str());

    std::wstring count = std::to_wstring(profiles.size());
    WritePrivateProfileStringW(L"Profiles", L"Count", count.c_str(), path.c_str());
    for (size_t i = 0; i < profiles.size(); ++i) {
        std::wstring idx = std::to_wstring(i);
        std::wstring idKey       = L"Id"       + idx;
        std::wstring nameKey     = L"Name"     + idx;
        std::wstring readOnlyKey = L"ReadOnly" + idx;
        WritePrivateProfileStringW(L"Profiles", idKey.c_str(),       profiles[i].id.c_str(),   path.c_str());
        WritePrivateProfileStringW(L"Profiles", nameKey.c_str(),     profiles[i].name.c_str(), path.c_str());
        WritePrivateProfileStringW(L"Profiles", readOnlyKey.c_str(), profiles[i].isReadOnly ? L"1" : L"0", path.c_str());
    }
    WritePrivateProfileStringW(L"Profiles", L"DefaultId", defaultProfileId.c_str(), path.c_str());
    Log(L"[Config] Wrote %d profile(s) to Shared.ini [Profiles].", (int)profiles.size());
}

// ── 小工具：讀 INI 字串 / 整數 ────────────────────────────────────────────

static std::wstring ReadString(const wchar_t* section, const wchar_t* key,
                               const wchar_t* defaultVal) {
    auto path = GetSharedIniPath();
    if (path.empty()) return std::wstring(defaultVal);
    WCHAR buf[256] = {};
    GetPrivateProfileStringW(section, key, defaultVal, buf, ARRAYSIZE(buf), path.c_str());
    return std::wstring(buf);
}

static int ReadInt(const wchar_t* section, const wchar_t* key, int defaultVal) {
    auto path = GetSharedIniPath();
    if (path.empty()) return defaultVal;
    return (int)GetPrivateProfileIntW(section, key, defaultVal, path.c_str());
}

// ── 內建手把映射偵測 ─────────────────────────────────────────────────────
//
// 偵測裝置是否內建廠商手把映射軟體（與 Mouse Mode 衝突需停用）。
// 目前僅涵蓋 ROG Ally / Ally X / Xbox Ally 家族（Armoury Crate SE）。
// 主程式、PhantomKey、PhantomLink 三處各自獨立偵測，不經 INI；
// 機型清單更新時必須三處同步修改：
//   - OmniConsole/Services/SettingsService.cs (HasBuiltInGamepadMapping)
//   - OmniConsole.PhantomKey/Config.cpp (此函式)
//   - OmniConsole.PhantomLink/Services/HardwareDetection.cs (HasBuiltInGamepadMapping)
static bool DetectBuiltInGamepadMappingImpl();

static bool DetectBuiltInGamepadMapping() {
    // 硬體型號執行期不變，首次偵測後快取
    static bool cached = DetectBuiltInGamepadMappingImpl();
    return cached;
}

static bool DetectBuiltInGamepadMappingImpl() {
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"HARDWARE\\DESCRIPTION\\System\\BIOS",
                      0, KEY_READ, &hKey) != ERROR_SUCCESS) return false;

    wchar_t buf[256] = {};
    DWORD cb = sizeof(buf), type = 0;
    LONG rc = RegQueryValueExW(hKey, L"SystemProductName", nullptr, &type,
                               reinterpret_cast<LPBYTE>(buf), &cb);
    RegCloseKey(hKey);
    if (rc != ERROR_SUCCESS || type != REG_SZ) return false;

    std::wstring upper(buf);
    for (auto& c : upper) c = (wchar_t)towupper(c);
    // ROG Ally 家族
    static const wchar_t* kKeywords[] = {
        L"RC71L", L"RC72L", L"RC72LA", L"RC73XA", L"RC73YA"
    };
    for (auto kw : kKeywords)
        if (upper.find(kw) != std::wstring::npos) return true;
    return false;
}

// MouseMode：新模型只有 On / Off。
// 舊值相容：Auto / ForceOn 一律視為 On；只有明確 Off 才停用。
static bool ParseMouseModeEnabled(const std::wstring& s) {
    return _wcsicmp(s.c_str(), L"Off") != 0;
}

// ============================================================================
// 公開介面
// ============================================================================

AppConfig ReadConfig() {
    AppConfig cfg = {};

    cfg.defaultPlatform = ReadString(L"General", L"DefaultPlatform", L"");

    cfg.steamOverlayEnabled = ReadInt(L"PhantomKey", L"SteamInGameOverlayEnabled", 1) != 0;

    cfg.mouseModeEnabled = ParseMouseModeEnabled(ReadString(L"PhantomKey", L"MouseMode", L"On"));

    cfg.hasBuiltInGamepadMapping = DetectBuiltInGamepadMapping();

    cfg.widgetActive = ReadInt(L"Status", L"WidgetActive", 0) != 0;

    Log(L"[Config] DefaultPlatform=%s, SteamOverlay=%d, MouseMode=%s, BuiltInMapping=%d, WidgetActive=%d",
        cfg.defaultPlatform.c_str(), (int)cfg.steamOverlayEnabled,
        cfg.mouseModeEnabled ? L"On" : L"Off", (int)cfg.hasBuiltInGamepadMapping,
        (int)cfg.widgetActive);
    return cfg;
}

// ── 目前 active profile id 寫入 ─────────────────────────────────────────────
//
// 靜態快取比對：相同值不寫，避免每 tick 觸發 mtime 變動。
void WriteActiveProfileId(const std::wstring& profileId) {
    auto path = GetSharedIniPath();
    if (path.empty()) return;
    static std::wstring lastWritten;
    if (profileId == lastWritten) return;
    if (WritePrivateProfileStringW(L"Status", L"ActiveProfileId", profileId.c_str(), path.c_str())) {
        lastWritten = profileId;
        Log(L"[Config] Wrote ActiveProfileId=\"%s\" to Shared.ini.", profileId.c_str());
    }
}
