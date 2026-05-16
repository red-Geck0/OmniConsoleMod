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

// ── 前景程式名寫入 ─────────────────────────────────────────────────────────
//
// 靜態快取比對：相同值不寫檔，避免每 tick 觸發 I/O。
void WriteForegroundProcess(const std::wstring& processName) {
    auto path = GetSharedIniPath();
    if (path.empty()) return;
    static std::wstring lastWritten;
    if (processName == lastWritten) return;
    if (WritePrivateProfileStringW(L"PhantomKey", L"ForegroundProcess",
                                   processName.c_str(), path.c_str())) {
        lastWritten = processName;
    }
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

static MouseModeState ParseMouseMode(const std::wstring& s) {
    if (_wcsicmp(s.c_str(), L"Off")       == 0) return MouseModeState::Off;
    if (_wcsicmp(s.c_str(), L"Blacklist") == 0) return MouseModeState::Blacklist;
    if (_wcsicmp(s.c_str(), L"Whitelist") == 0) return MouseModeState::Whitelist;
    if (_wcsicmp(s.c_str(), L"OmniList")  == 0) return MouseModeState::OmniList;
    // Migration: nilai lama dari versi sebelumnya
    if (_wcsicmp(s.c_str(), L"ForceOn")   == 0) return MouseModeState::Blacklist;
    if (_wcsicmp(s.c_str(), L"Auto")      == 0) return MouseModeState::Whitelist;
    return MouseModeState::Whitelist; // default
}

static const wchar_t* MouseModeToStr(MouseModeState m) {
    switch (m) {
        case MouseModeState::Off:       return L"Off";
        case MouseModeState::Blacklist: return L"Blacklist";
        case MouseModeState::OmniList:  return L"OmniList";
        default:                        return L"Whitelist";
    }
}

// Parse CSV string menjadi vector<wstring>, trim spasi setiap token
static std::vector<std::wstring> ParseCsv(const std::wstring& s) {
    std::vector<std::wstring> result;
    std::wstring token;
    for (size_t i = 0; i <= s.size(); ++i) {
        if (i == s.size() || s[i] == L',') {
            // trim leading/trailing spaces
            size_t start = token.find_first_not_of(L' ');
            size_t end   = token.find_last_not_of(L' ');
            if (start != std::wstring::npos)
                result.push_back(token.substr(start, end - start + 1));
            token.clear();
        } else {
            token += s[i];
        }
    }
    return result;
}

// ============================================================================
// 公開介面
// ============================================================================

// ── Button name table (sinkron dengan ButtonIdx di Config.h) ──────────────

const wchar_t* const kButtonNames[BTN_COUNT] = {
    L"A", L"B", L"X", L"Y",
    L"LB", L"RB", L"LT", L"RT",
    L"LSPress", L"RSPress",
    L"DPadUp", L"DPadDown", L"DPadLeft", L"DPadRight"
};

int ButtonNameToIdx(const std::wstring& name) {
    for (int i = 0; i < BTN_COUNT; i++)
        if (_wcsicmp(name.c_str(), kButtonNames[i]) == 0) return i;
    return -1;
}

// Default mapping (sinkron dengan SettingsService.cs::GetDefaultButtonMapping).
// Dipakai sebagai fallback saat INI belum punya entry.
static const wchar_t* DefaultMapping(int btnIdx, bool classic) {
    if (classic) {
        static const wchar_t* defs[BTN_COUNT] = {
            L"enter", L"esc", L"pgdn", L"pgup",
            L"tab", L"lclick", L"shift+tab", L"rclick",
            L"", L"",
            L"up", L"down", L"left", L"right"
        };
        return defs[btnIdx];
    }
    static const wchar_t* defs[BTN_COUNT] = {
        L"lclick", L"rclick", L"pgdn", L"pgup",
        L"ctrl+shift+tab", L"ctrl+tab", L"esc", L"enter",
        L"shift+tab", L"tab",
        L"up", L"down", L"left", L"right"
    };
    return defs[btnIdx];
}

AppConfig ReadConfig() {
    AppConfig cfg = {};

    cfg.defaultPlatform = ReadString(L"General", L"DefaultPlatform", L"");

    cfg.steamOverlayEnabled = ReadInt(L"PhantomKey", L"SteamInGameOverlayEnabled", 1) != 0;

    cfg.mouseMode = ParseMouseMode(ReadString(L"PhantomKey", L"MouseMode", L"Whitelist"));

    std::wstring layout = ReadString(L"PhantomKey", L"MouseModeLayout", L"OmniNav");
    if (_wcsicmp(layout.c_str(), L"Classic") != 0) layout = L"OmniNav";
    cfg.mouseModeLayout = layout;

    int rawPct = ReadInt(L"PhantomKey", L"CursorSpeedPercent", 100);
    static const int kValidPercents[] = { 25, 50, 75, 100, 125, 150, 175, 200 };
    cfg.cursorSpeedPercent = 100;
    for (int p : kValidPercents) if (p == rawPct) { cfg.cursorSpeedPercent = p; break; }

    cfg.hasBuiltInGamepadMapping = DetectBuiltInGamepadMapping();

    // ── App lists untuk Whitelist / Blacklist ────────────────────────────────
    // Default whitelist: browser + EpicGamesLauncher (explorer & steamwebhelper via special detection)
    // Default blacklist: OmniConsole + Playnite (hardcoded special detection tetap jalan di atas list ini)
    cfg.mouseModeWhitelist = ParseCsv(ReadString(L"MouseMode.Whitelist", L"Apps",
        L"msedge,chrome,firefox,opera,brave,EpicGamesLauncher,Discord"));
    cfg.mouseModeBlacklist = ParseCsv(ReadString(L"MouseMode.Blacklist", L"Apps",
        L"OmniConsole,Playnite.FullscreenApp"));

    // ── Button mappings per layout ───────────────────────────────────────────
    for (int i = 0; i < BTN_COUNT; i++) {
        cfg.mapOmniNav[i] = ReadString(L"Mapping.OmniNav", kButtonNames[i], DefaultMapping(i, false));
        cfg.mapClassic[i] = ReadString(L"Mapping.Classic", kButtonNames[i], DefaultMapping(i, true));
    }

    // ── Layered Mode per layout ──────────────────────────────────────────────
    cfg.layeredEnabledOmniNav = ReadInt(L"LayeredMode.OmniNav", L"Enabled", 0) != 0;
    std::wstring trigOmni = ReadString(L"LayeredMode.OmniNav", L"TriggerButton", L"RSPress");
    int idxOmni = ButtonNameToIdx(trigOmni);
    cfg.layeredButtonOmniNav = (idxOmni >= 0) ? idxOmni : BTN_RSPress;

    cfg.layeredEnabledClassic = ReadInt(L"LayeredMode.Classic", L"Enabled", 0) != 0;
    std::wstring trigCls = ReadString(L"LayeredMode.Classic", L"TriggerButton", L"RSPress");
    int idxCls = ButtonNameToIdx(trigCls);
    cfg.layeredButtonClassic = (idxCls >= 0) ? idxCls : BTN_RSPress;

    Log(L"[Config] DefaultPlatform=%s, SteamOverlay=%d, MouseMode=%s, Layout=%s, CursorSpeed=%d%%, BuiltInMapping=%d, LayeredOmni=%d/%s, LayeredClassic=%d/%s",
        cfg.defaultPlatform.c_str(), (int)cfg.steamOverlayEnabled,
        MouseModeToStr(cfg.mouseMode), cfg.mouseModeLayout.c_str(),
        cfg.cursorSpeedPercent, (int)cfg.hasBuiltInGamepadMapping,
        (int)cfg.layeredEnabledOmniNav, kButtonNames[cfg.layeredButtonOmniNav],
        (int)cfg.layeredEnabledClassic, kButtonNames[cfg.layeredButtonClassic]);
    return cfg;
}
