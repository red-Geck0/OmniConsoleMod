using OmniConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理應用程式設定的持久化讀寫。
    /// UI 狀態儲存於 ApplicationData.Current.LocalSettings。
    /// PhantomKey / PhantomLink 共用的設定寫入 PublisherCacheFolder\OmniConsoleShared\Shared.ini，
    /// 同 Publisher (CN=8bit2qubit) 的 MSIX 套件皆可共用。
    /// </summary>
    public static class SettingsService
    {
        // ─── 共用 INI（同 Publisher 各套件共通） ──────────────────────
        private const string SharedFolderName = "OmniConsoleShared";
        private const string SharedIniFileName = "Shared.ini";

        private static string? _cachedSharedIniPath;
        private static string SharedIniPath
        {
            get
            {
                if (_cachedSharedIniPath != null) return _cachedSharedIniPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedSharedIniPath = Path.Combine(folder.Path, SharedIniFileName);
                }
                catch
                {
                    _cachedSharedIniPath = string.Empty;
                }
                return _cachedSharedIniPath;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetPrivateProfileStringW(
            string? lpAppName, string? lpKeyName, string? lpDefault,
            System.Text.StringBuilder? lpReturnedString, int nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileStringW(
            string lpAppName, string lpKeyName, string? lpString, string lpFileName);

        /// <summary>
        /// 寫入共用 INI；PublisherCacheFolder 無法取得時靜默略過，避免影響 LocalSettings 主路徑。
        /// </summary>
        private static void WriteShared(string section, string name, string value)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path)) return;
            try { WritePrivateProfileStringW(section, name, value, path); } catch { }
        }

        /// <summary>
        /// 從共用 INI 讀值；路徑取不到或檔案不存在時回 defaultValue。
        /// </summary>
        private static string ReadShared(string section, string name, string defaultValue)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return defaultValue;
            var sb = new System.Text.StringBuilder(256);
            GetPrivateProfileStringW(section, name, defaultValue, sb, sb.Capacity, path);
            return sb.ToString();
        }

        // ─── 舊版 INI 遷移（LocalCache\OmniConsole\OmniConsole.ini） ───────
        private static readonly string LegacyIniDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniConsole");

        private static readonly string LegacyIniPath = Path.Combine(LegacyIniDir, "OmniConsole.ini");

        private const string DefaultPlatformKey = "DefaultPlatform";
        private const string LastLaunchedVersionKey = "LastLaunchedVersion";

        /// <summary>
        /// 將 LocalSettings 與啟動時偵測值同步至共用 INI，
        /// 確保即使使用者從未手動切換設定，PhantomKey 與 PhantomLink 仍能讀取正確值。
        /// 僅在首次安裝或版本更新時呼叫（由 IsFirstRunOrUpdate() 判斷）。
        /// </summary>
        public static void SyncPhantomKeyStore()
        {
            WriteShared("General", "DefaultPlatform", GetDefaultPlatform().Id);
            WriteShared("PhantomKey", "SteamInGameOverlayEnabled", GetUsePhantomKeySteamInGameOverlay() ? "1" : "0");
            WriteShared("PhantomKey", "MouseMode", GetMouseMode());
            WriteShared("PhantomKey", "MouseModeLayout", GetMouseModeLayout());
            WriteShared("PhantomKey", "CursorSpeedPercent", GetCursorSpeedPercent().ToString());
            WriteShared("MouseMode.Whitelist", "Apps", string.Join(",", GetMouseModeWhitelist()));
            WriteShared("MouseMode.Blacklist", "Apps", string.Join(",", GetMouseModeBlacklist()));

            // Sync semua mapping & Layered Mode untuk kedua layout
            foreach (var layout in new[] { LayoutOmniNav, LayoutClassic })
            {
                WriteShared($"LayeredMode.{layout}", "Enabled", GetLayeredModeEnabled(layout) ? "1" : "0");
                WriteShared($"LayeredMode.{layout}", "TriggerButton", GetLayeredModeButton(layout));
                foreach (var btn in AllMappableButtons)
                    WriteShared($"Mapping.{layout}", btn, GetButtonMapping(layout, btn));
            }
        }

        /// <summary>
        /// 從共用 INI 重新同步值到 LocalSettings。
        /// 外部行程（PhantomLink）可能直接改動 Shared.ini，
        /// 本方法讓主程式 UI 重新讀取時能拿到最新狀態。
        /// </summary>
        public static void ReloadFromSharedStore()
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                string defaultPlatform = ReadShared("General", "DefaultPlatform", "");
                if (!string.IsNullOrEmpty(defaultPlatform))
                    settings.Values[DefaultPlatformKey] = defaultPlatform;

                string mouseMode = MigrateMouseMode(ReadShared("PhantomKey", "MouseMode", MouseModeWhitelist));
                if (mouseMode == MouseModeOff || mouseMode == MouseModeWhitelist || mouseMode == MouseModeBlacklist)
                    settings.Values["MouseMode"] = mouseMode;

                // Reload app lists
                string wlApps = ReadShared("MouseMode.Whitelist", "Apps", DefaultWhitelistApps);
                if (!string.IsNullOrWhiteSpace(wlApps))
                    settings.Values["MouseModeWhitelistApps"] = wlApps;
                string blApps = ReadShared("MouseMode.Blacklist", "Apps", DefaultBlacklistApps);
                if (!string.IsNullOrWhiteSpace(blApps))
                    settings.Values["MouseModeBlacklistApps"] = blApps;

                string layout = ReadShared("PhantomKey", "MouseModeLayout", LayoutOmniNav);
                if (layout == LayoutOmniNav || layout == LayoutClassic)
                    settings.Values["MouseModeLayout"] = layout;

                string pctStr = ReadShared("PhantomKey", "CursorSpeedPercent", "100");
                if (int.TryParse(pctStr, out int pct))
                {
                    foreach (var p in ValidCursorSpeedPercents)
                        if (p == pct) { settings.Values["CursorSpeedPercent"] = pct; break; }
                }

                string steamOverlay = ReadShared("PhantomKey", "SteamInGameOverlayEnabled", "1");
                settings.Values["UsePhantomKeySteamInGameOverlay"] = steamOverlay != "0";

                // Reload mapping & Layered Mode untuk kedua layout
                foreach (var lyt in new[] { LayoutOmniNav, LayoutClassic })
                {
                    string en = ReadShared($"LayeredMode.{lyt}", "Enabled", "0");
                    settings.Values[$"LayeredEnabled_{lyt}"] = en != "0";

                    string trig = ReadShared($"LayeredMode.{lyt}", "TriggerButton", "RSPress");
                    if (Array.IndexOf(AllMappableButtons, trig) >= 0)
                        settings.Values[$"LayeredBtn_{lyt}"] = trig;

                    foreach (var btn in AllMappableButtons)
                    {
                        string defVal = GetDefaultButtonMapping(lyt, btn);
                        string m = ReadShared($"Mapping.{lyt}", btn, defVal);
                        settings.Values[$"BtnMap_{lyt}_{btn}"] = m;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 一次性遷移：若共用 INI 尚未建立 → 嘗試從舊版 LocalCache INI。
        /// 僅在首次安裝或版本更新時呼叫（由 IsFirstRunOrUpdate() 判斷）。
        /// </summary>
        public static void MigrateLegacyIfNeeded()
        {
            try
            {
                var sharedPath = SharedIniPath;
                if (string.IsNullOrEmpty(sharedPath)) return;

                // 僅在共用 INI 尚未建立時才從舊檔讀值（避免覆蓋使用者後續修改）
                if (!File.Exists(sharedPath) && File.Exists(LegacyIniPath))
                {
                    string oldMouseMode = ReadLegacyIni("PhantomKey", "MouseModeEnabled", "1");
                    ApplicationData.Current.LocalSettings.Values["MouseMode"] =
                        oldMouseMode == "0" ? "Off" : "Auto";

                    string layout = ReadLegacyIni("PhantomKey", "MouseModeLayout", "OmniNav");
                    if (layout != "OmniNav" && layout != "Classic") layout = "OmniNav";
                    ApplicationData.Current.LocalSettings.Values["MouseModeLayout"] = layout;

                    if (int.TryParse(ReadLegacyIni("PhantomKey", "CursorSpeedPercent", "100"), out var pct))
                        ApplicationData.Current.LocalSettings.Values["CursorSpeedPercent"] = pct;

                    string steamOverlay = ReadLegacyIni("PhantomKey", "SteamInGameOverlayEnabled", "1");
                    ApplicationData.Current.LocalSettings.Values["UsePhantomKeySteamInGameOverlay"] = steamOverlay != "0";

                    SyncPhantomKeyStore();
                }

                // 舊檔清理：與是否需要讀值無關，只要殘留就刪
                if (File.Exists(LegacyIniPath))
                {
                    try { File.Delete(LegacyIniPath); } catch { }
                    try
                    {
                        if (Directory.Exists(LegacyIniDir) && Directory.GetFileSystemEntries(LegacyIniDir).Length == 0)
                            Directory.Delete(LegacyIniDir);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string ReadLegacyIni(string section, string key, string defaultValue)
        {
            var sb = new System.Text.StringBuilder(256);
            GetPrivateProfileStringW(section, key, defaultValue, sb, sb.Capacity, LegacyIniPath);
            return sb.ToString();
        }

        public static string GetAppVersion()
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// 判斷是否為首次啟動（尚未設定預設平台），或為更新後的首次啟動。
        /// </summary>
        public static bool IsFirstRunOrUpdate()
        {
            var settings = ApplicationData.Current.LocalSettings;

            // 若尚未設定平台，必為首次安裝啟動
            if (!settings.Values.ContainsKey(DefaultPlatformKey))
                return true;

            // 若已設定平台，檢查是否為剛更新版本
            if (settings.Values.TryGetValue(LastLaunchedVersionKey, out object? value) && value is string lastVersion)
                return lastVersion != GetAppVersion();

            // 若無版本紀錄（例如從舊版升級），亦視為需重新確認的更新啟動
            return true;
        }

        /// <summary>
        /// 儲存目前應用程式的版本號以供下次啟動比對。
        /// </summary>
        public static void SaveCurrentVersion()
        {
            ApplicationData.Current.LocalSettings.Values[LastLaunchedVersionKey] = GetAppVersion();
        }

        /// <summary>
        /// 取得使用者設定的預設遊戲平台。
        /// 儲存值為平台 Id 字串；若找不到對應的平台定義，則回退至清單中的第一個平台。
        /// </summary>
        public static PlatformDefinition GetDefaultPlatform()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(DefaultPlatformKey, out object? value) && value is string id)
            {
                // 先查系統平台，再查使用者自訂平台
                return PlatformCatalog.FindById(id)
                    ?? UserPlatformStore.FindById(id)
                    ?? PlatformCatalog.All[0];
            }
            return PlatformCatalog.All[0];
        }

        /// <summary>
        /// 儲存使用者選擇的預設遊戲平台（以 Id 字串持久化）。
        /// </summary>
        public static void SetDefaultPlatform(PlatformDefinition platform)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(DefaultPlatformKey, out object? prev) && prev is string id && id == platform.Id)
                return;
            settings.Values[DefaultPlatformKey] = platform.Id;
            WriteShared("General", "DefaultPlatform", platform.Id);
        }
        /// <summary>
        /// 取得是否啟用「Game Bar 媒體櫃按鈕進入設定介面」功能。
        /// 預設為 true。
        /// 註：自 v1.9.0.0 起 SettingsPage 已隱藏此開關，強制 return true 忽略 LocalSettings 中的舊值，
        /// 避免使用者過去若關過此項，升級後從 Game Bar 媒體櫃進不來、又無 UI 可改回的死結。
        /// 待開發人員模式頁完工後再還原讀取 LocalSettings 的邏輯。
        /// </summary>
        public static bool GetUseGameBarLibraryForSettings()
        {
            return true;
            // var settings = ApplicationData.Current.LocalSettings;
            // if (settings.Values.TryGetValue("UseGameBarLibraryForSettings", out object? value) && value is bool isEnabled)
            //     return isEnabled;
            // return true;
        }

        /// <summary>
        /// 儲存是否啟用「Game Bar 媒體櫃按鈕進入設定介面」功能。
        /// </summary>
        public static void SetUseGameBarLibraryForSettings(bool isEnabled)
        {
            ApplicationData.Current.LocalSettings.Values["UseGameBarLibraryForSettings"] = isEnabled;
        }

        /// <summary>
        /// 取得是否啟用「Game Bar 平台對接 (Passthrough)」功能。
        /// 預設為 false。
        /// 註：自 v1.9.0.0 起 SettingsPage 已隱藏此開關，強制 return false 忽略 LocalSettings 中的舊值，
        /// 避免使用者過去若開過 Passthrough，升級後 Game Bar 媒體櫃按鈕直接導向預設平台、無 UI 可關回的死結。
        /// 待開發人員模式頁完工後再還原讀取 LocalSettings 的邏輯。
        /// </summary>
        public static bool GetEnablePassthrough()
        {
            return false;
            // var settings = ApplicationData.Current.LocalSettings;
            // if (settings.Values.TryGetValue("EnablePassthrough", out object? value) && value is bool isEnabled)
            //     return isEnabled;
            // return false;
        }

        /// <summary>
        /// 儲存是否啟用「Game Bar 平台對接 (Passthrough)」功能。
        /// </summary>
        public static void SetEnablePassthrough(bool isEnabled)
        {
            ApplicationData.Current.LocalSettings.Values["EnablePassthrough"] = isEnabled;
        }

        /// <summary>
        /// 取得使用者是否已接受自訂平台實驗性功能的免責聲明。
        /// </summary>
        public static bool GetCustomPlatformConsentAccepted()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CustomPlatformConsentAccepted", out object? value) && value is bool accepted)
                return accepted;
            return false;
        }

        /// <summary>
        /// 儲存使用者已接受自訂平台實驗性功能的免責聲明。
        /// </summary>
        public static void SetCustomPlatformConsentAccepted(bool accepted)
        {
            ApplicationData.Current.LocalSettings.Values["CustomPlatformConsentAccepted"] = accepted;
        }

        /// <summary>
        /// 取得是否啟用自動檢查更新。
        /// 預設為 false。
        /// </summary>
        public static bool GetAutoUpdateCheckEnabled()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("AutoUpdateCheckEnabled", out object? value) && value is bool enabled)
                return enabled;
            return false;
        }

        /// <summary>
        /// 儲存是否啟用自動檢查更新。
        /// </summary>
        public static void SetAutoUpdateCheckEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values["AutoUpdateCheckEnabled"] = enabled;
        }

        /// <summary>
        /// 取得上次檢查更新的日期（"yyyy-MM-dd" 格式）。
        /// </summary>
        public static string GetLastUpdateCheckDate()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("LastUpdateCheckDate", out object? value) && value is string date)
                return date;
            return "";
        }

        /// <summary>
        /// 儲存上次檢查更新的日期。
        /// </summary>
        public static void SetLastUpdateCheckDate(string date)
        {
            ApplicationData.Current.LocalSettings.Values["LastUpdateCheckDate"] = date;
        }

        /// <summary>
        /// 取得快取的最新可用版本號（如 "1.3.0.0"）。
        /// 空字串表示無新版或尚未檢查。
        /// </summary>
        public static string GetCachedNewVersion()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedNewVersion", out object? value) && value is string version)
                return version;
            return "";
        }

        /// <summary>
        /// 儲存快取的最新可用版本號。
        /// </summary>
        public static void SetCachedNewVersion(string version)
        {
            ApplicationData.Current.LocalSettings.Values["CachedNewVersion"] = version;
        }

        /// <summary>
        /// 清除快取的最新可用版本號（表示已是最新版）。
        /// </summary>
        public static void ClearCachedNewVersion()
        {
            ApplicationData.Current.LocalSettings.Values["CachedNewVersion"] = "";
        }

        /// <summary>
        /// 取得快取的 .msix 下載 URL。
        /// 空字串表示無可用下載連結。
        /// </summary>
        public static string GetCachedDownloadUrl()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedDownloadUrl", out object? value) && value is string url)
                return url;
            return "";
        }

        /// <summary>
        /// 儲存快取的 .msix 下載 URL。
        /// </summary>
        public static void SetCachedDownloadUrl(string url)
        {
            ApplicationData.Current.LocalSettings.Values["CachedDownloadUrl"] = url;
        }

        /// <summary>
        /// 清除快取的下載 URL。
        /// </summary>
        public static void ClearCachedDownloadUrl()
        {
            ApplicationData.Current.LocalSettings.Values["CachedDownloadUrl"] = "";
        }

        // ── PhantomLink 更新快取 ──────────────────────────────────────────

        /// <summary>
        /// 取得快取的更新類型（"None" | "MissingPhantomLink" | "MainAppUpdate"）。
        /// 供 InfoBar 判斷顯示何種通知。
        /// </summary>
        public static string GetCachedUpdateKind()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedUpdateKind", out object? value) && value is string kind)
                return kind;
            return "";
        }

        /// <summary>
        /// 儲存快取的更新類型。
        /// </summary>
        public static void SetCachedUpdateKind(string kind)
        {
            ApplicationData.Current.LocalSettings.Values["CachedUpdateKind"] = kind;
        }

        /// <summary>
        /// 取得快取的 PhantomLink .msix 下載 URL。
        /// 空字串表示無可用下載連結。
        /// </summary>
        public static string GetCachedPhantomLinkUrl()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedPhantomLinkUrl", out object? value) && value is string url)
                return url;
            return "";
        }

        /// <summary>
        /// 儲存快取的 PhantomLink .msix 下載 URL。
        /// </summary>
        public static void SetCachedPhantomLinkUrl(string url)
        {
            ApplicationData.Current.LocalSettings.Values["CachedPhantomLinkUrl"] = url;
        }

        /// <summary>
        /// 清除快取的 PhantomLink 下載 URL。
        /// </summary>
        public static void ClearCachedPhantomLinkUrl()
        {
            ApplicationData.Current.LocalSettings.Values["CachedPhantomLinkUrl"] = "";
        }

        /// <summary>
        /// 取得是否有待完成的設定頁重啟。
        /// PhantomLink 安裝完成後透過 RequestRestartAsync 重啟時設為 true，
        /// Program.cs 讀取後導向設定頁並清除。
        /// </summary>
        public static bool GetPendingSettingsRestart()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("PendingSettingsRestart", out object? value) && value is bool flag)
                return flag;
            return false;
        }

        /// <summary>
        /// 設定待完成的設定頁重啟旗標。
        /// </summary>
        public static void SetPendingSettingsRestart(bool value)
        {
            ApplicationData.Current.LocalSettings.Values["PendingSettingsRestart"] = value;
        }

        // ── 中斷可恢復的更新狀態（拔電/強制重開後續做）────────────

        /// <summary>
        /// 取得目前的待續更新階段。空字串=無；"Phase1"=PhantomLink 尚未完成；"Phase2"=PhantomLink 已裝、OmniConsole 尚未完成。
        /// </summary>
        public static string GetPendingUpdatePhase()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("PendingUpdatePhase", out object? value) && value is string phase)
                return phase;
            return "";
        }

        /// <summary>設定待續更新階段。</summary>
        public static void SetPendingUpdatePhase(string phase)
        {
            ApplicationData.Current.LocalSettings.Values["PendingUpdatePhase"] = phase;
        }

        /// <summary>清除待續更新狀態（包含 phase 與三個 URL/版本欄位）。</summary>
        public static void ClearPendingUpdate()
        {
            var v = ApplicationData.Current.LocalSettings.Values;
            v["PendingUpdatePhase"] = "";
            v["PendingUpdatePhantomLinkUrl"] = "";
            v["PendingUpdateMainUrl"] = "";
            v["PendingUpdateTargetVersion"] = "";
        }

        /// <summary>取得待續更新的 PhantomLink 下載 URL。</summary>
        public static string GetPendingUpdatePhantomLinkUrl()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("PendingUpdatePhantomLinkUrl", out object? value) && value is string url)
                return url;
            return "";
        }

        /// <summary>儲存待續更新的 PhantomLink 下載 URL。</summary>
        public static void SetPendingUpdatePhantomLinkUrl(string url)
        {
            ApplicationData.Current.LocalSettings.Values["PendingUpdatePhantomLinkUrl"] = url;
        }

        /// <summary>取得待續更新的 OmniConsole 下載 URL。</summary>
        public static string GetPendingUpdateMainUrl()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("PendingUpdateMainUrl", out object? value) && value is string url)
                return url;
            return "";
        }

        /// <summary>儲存待續更新的 OmniConsole 下載 URL。</summary>
        public static void SetPendingUpdateMainUrl(string url)
        {
            ApplicationData.Current.LocalSettings.Values["PendingUpdateMainUrl"] = url;
        }

        /// <summary>取得待續更新的目標版本字串（與 GetAppVersion 同格式）。</summary>
        public static string GetPendingUpdateTargetVersion()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("PendingUpdateTargetVersion", out object? value) && value is string version)
                return version;
            return "";
        }

        /// <summary>儲存待續更新的目標版本字串。</summary>
        public static void SetPendingUpdateTargetVersion(string version)
        {
            ApplicationData.Current.LocalSettings.Values["PendingUpdateTargetVersion"] = version;
        }

        /// <summary>
        /// 取得是否啟用 PhantomKey 手把輸入服務（⧉ 鍵開啟平台選單）。
        /// 預設為 true。
        /// </summary>
        public static bool GetUsePhantomKey()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKey", out object? value) && value is bool enabled)
                return enabled;
            return true;
        }

        /// <summary>
        /// 儲存是否啟用 PhantomKey 手把輸入服務。
        /// </summary>
        public static void SetUsePhantomKey(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values["UsePhantomKey"] = enabled;
        }

        /// <summary>
        /// 取得是否啟用 Steam In-Game Overlay（長按 ☰ 送出 Overlay 快速鍵）。
        /// 預設為 true。
        /// </summary>
        public static bool GetUsePhantomKeySteamInGameOverlay()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeySteamInGameOverlay", out object? value) && value is bool enabled)
                return enabled;
            return true;
        }

        /// <summary>
        /// 儲存是否啟用 Steam In-Game Overlay，同步寫入 INI 供 PhantomKey 讀取。
        /// </summary>
        public static void SetUsePhantomKeySteamInGameOverlay(bool enabled)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeySteamInGameOverlay", out object? prev) && prev is bool val && val == enabled)
                return;
            settings.Values["UsePhantomKeySteamInGameOverlay"] = enabled;
            WriteShared("PhantomKey", "SteamInGameOverlayEnabled", enabled ? "1" : "0");
        }

        // ─── Gamepad Mouse Mode（3-way: Off/Whitelist/Blacklist） ───────────

        public const string MouseModeOff = "Off";
        public const string MouseModeWhitelist = "Whitelist";   // dulu "Auto"
        public const string MouseModeBlacklist = "Blacklist";   // dulu "ForceOn"

        // Default app list CSV values (sinkron dengan C++ Config.cpp default)
        private const string DefaultWhitelistApps = "msedge,chrome,firefox,opera,brave,EpicGamesLauncher";
        private const string DefaultBlacklistApps = "OmniConsole,Playnite.FullscreenApp,OneGameLauncher";

        /// <summary>Konversi nilai mode lama (Auto/ForceOn) ke nilai baru (Whitelist/Blacklist).</summary>
        private static string MigrateMouseMode(string mode) => mode switch
        {
            "Auto"    => MouseModeWhitelist,
            "ForceOn" => MouseModeBlacklist,
            _         => mode
        };

        /// <summary>
        /// 取得 Mouse Mode（"Off" / "Whitelist" / "Blacklist"）。
        /// 無效或缺失值回 "Whitelist"。Auto-migrate nilai lama "Auto"/"ForceOn".
        /// </summary>
        public static string GetMouseMode()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseMode", out object? value) && value is string str)
            {
                str = MigrateMouseMode(str);
                if (str == MouseModeOff || str == MouseModeWhitelist || str == MouseModeBlacklist)
                    return str;
            }
            return MouseModeWhitelist;
        }

        /// <summary>
        /// 儲存 Mouse Mode，未知值回退至 "Whitelist"，同步寫入 INI；值未變動時直接略過避免多餘寫入。
        /// </summary>
        public static void SetMouseMode(string mode)
        {
            if (mode != MouseModeOff && mode != MouseModeWhitelist && mode != MouseModeBlacklist)
                mode = MouseModeWhitelist;
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseMode", out object? prev) && prev is string pv && pv == mode)
                return;
            settings.Values["MouseMode"] = mode;
            WriteShared("PhantomKey", "MouseMode", mode);
        }

        // ─── Mouse Mode App Lists (Whitelist / Blacklist) ────────────────────

        /// <summary>Baca whitelist apps dari LocalSettings. Default: browser + EpicGamesLauncher.</summary>
        public static List<string> GetMouseModeWhitelist()
        {
            var settings = ApplicationData.Current.LocalSettings;
            string csv = (settings.Values.TryGetValue("MouseModeWhitelistApps", out object? v) && v is string s && s.Length > 0)
                ? s : DefaultWhitelistApps;
            return [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        /// <summary>Simpan whitelist apps dan sync ke Shared.ini [MouseMode.Whitelist] Apps=</summary>
        public static void SetMouseModeWhitelist(List<string> apps)
        {
            string csv = string.Join(",", apps.Select(a => a.Trim()).Where(a => a.Length > 0));
            ApplicationData.Current.LocalSettings.Values["MouseModeWhitelistApps"] = csv;
            WriteShared("MouseMode.Whitelist", "Apps", csv.Length > 0 ? csv : DefaultWhitelistApps);
        }

        /// <summary>Baca blacklist apps dari LocalSettings. Default: OmniConsole + Playnite.</summary>
        public static List<string> GetMouseModeBlacklist()
        {
            var settings = ApplicationData.Current.LocalSettings;
            string csv = (settings.Values.TryGetValue("MouseModeBlacklistApps", out object? v) && v is string s && s.Length > 0)
                ? s : DefaultBlacklistApps;
            return [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        /// <summary>Simpan blacklist apps dan sync ke Shared.ini [MouseMode.Blacklist] Apps=</summary>
        public static void SetMouseModeBlacklist(List<string> apps)
        {
            string csv = string.Join(",", apps.Select(a => a.Trim()).Where(a => a.Length > 0));
            ApplicationData.Current.LocalSettings.Values["MouseModeBlacklistApps"] = csv;
            WriteShared("MouseMode.Blacklist", "Apps", csv.Length > 0 ? csv : DefaultBlacklistApps);
        }

        /// <summary>OmniNav 預設版面配置。</summary>
        public const string LayoutOmniNav = "OmniNav";
        /// <summary>Classic 版面配置。</summary>
        public const string LayoutClassic = "Classic";

        /// <summary>
        /// 取得 Mouse Mode 按鍵配置（"OmniNav" 或 "Classic"）。預設 "OmniNav"。
        /// </summary>
        public static string GetMouseModeLayout()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseModeLayout", out object? value) && value is string str
                && (str == LayoutOmniNav || str == LayoutClassic))
                return str;
            return LayoutOmniNav;
        }

        /// <summary>
        /// 儲存 Mouse Mode 按鍵配置，未知值回退至 "OmniNav"，同步寫入 INI。
        /// </summary>
        public static void SetMouseModeLayout(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) layout = LayoutOmniNav;
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseModeLayout", out object? prev) && prev is string pv && pv == layout)
                return;
            settings.Values["MouseModeLayout"] = layout;
            WriteShared("PhantomKey", "MouseModeLayout", layout);
        }

        public static readonly int[] ValidCursorSpeedPercents = { 25, 50, 75, 100, 125, 150, 175, 200 };

        /// <summary>
        /// 取得游標速度百分比，限制為 25/50/75/100/125/150/175/200。預設 100。
        /// </summary>
        public static int GetCursorSpeedPercent()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CursorSpeedPercent", out object? value) && value is int pct)
            {
                foreach (var p in ValidCursorSpeedPercents)
                    if (p == pct) return p;
            }
            return 100;
        }

        /// <summary>
        /// 儲存游標速度百分比（限制為合法檔位），同步寫入 INI。
        /// </summary>
        public static void SetCursorSpeedPercent(int percent)
        {
            int valid = 100;
            foreach (var p in ValidCursorSpeedPercents)
                if (p == percent) { valid = p; break; }
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CursorSpeedPercent", out object? prev) && prev is int pv && pv == valid)
                return;
            settings.Values["CursorSpeedPercent"] = valid;
            WriteShared("PhantomKey", "CursorSpeedPercent", valid.ToString());
        }

        // ─── Button Mapping & Layered Mode (per-layout) ─────────────────────

        /// <summary>14 ID tombol controller yang konfigurable.</summary>
        public static readonly string[] AllMappableButtons =
        [
            "A", "B", "X", "Y",
            "LB", "RB", "LT", "RT",
            "LSPress", "RSPress",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
        ];

        /// <summary>
        /// Default mapping bawaan untuk layout tertentu (sumber: MouseMode.cpp hardcoded).
        /// Format: "modifier+modifier+key" atau token khusus (lclick/rclick/wheelup/dll).
        /// </summary>
        public static string GetDefaultButtonMapping(string layout, string button)
        {
            if (layout == LayoutClassic)
            {
                return button switch
                {
                    "A" => "enter",
                    "B" => "esc",
                    "X" => "pgdn",
                    "Y" => "pgup",
                    "LB" => "tab",
                    "RB" => "lclick",
                    "LT" => "shift+tab",
                    "RT" => "rclick",
                    "LSPress" => "",
                    "RSPress" => "",
                    "DPadUp" => "up",
                    "DPadDown" => "down",
                    "DPadLeft" => "left",
                    "DPadRight" => "right",
                    _ => "",
                };
            }
            // OmniNav (default)
            return button switch
            {
                "A" => "lclick",
                "B" => "rclick",
                "X" => "pgdn",
                "Y" => "pgup",
                "LB" => "ctrl+shift+tab",
                "RB" => "ctrl+tab",
                "LT" => "esc",
                "RT" => "enter",
                "LSPress" => "shift+tab",
                "RSPress" => "tab",
                "DPadUp" => "up",
                "DPadDown" => "down",
                "DPadLeft" => "left",
                "DPadRight" => "right",
                _ => "",
            };
        }

        /// <summary>
        /// Ambil mapping tombol untuk layout tertentu. Jika belum di-set, kembalikan default bawaan.
        /// </summary>
        public static string GetButtonMapping(string layout, string button)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return "";
            if (string.IsNullOrEmpty(button)) return "";

            var settings = ApplicationData.Current.LocalSettings;
            string key = $"BtnMap_{layout}_{button}";
            if (settings.Values.TryGetValue(key, out object? value) && value is string str)
                return str;
            return GetDefaultButtonMapping(layout, button);
        }

        /// <summary>Simpan mapping tombol; sync ke INI section [Mapping.&lt;layout&gt;].</summary>
        public static void SetButtonMapping(string layout, string button, string mapping)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return;
            if (string.IsNullOrEmpty(button)) return;
            mapping ??= "";

            var settings = ApplicationData.Current.LocalSettings;
            string key = $"BtnMap_{layout}_{button}";
            if (settings.Values.TryGetValue(key, out object? prev) && prev is string pv && pv == mapping)
                return;
            settings.Values[key] = mapping;
            WriteShared($"Mapping.{layout}", button, mapping);
        }

        /// <summary>Reset semua 14 mapping di layout ke default bawaan.</summary>
        public static void ResetAllButtonMappings(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return;
            foreach (var btn in AllMappableButtons)
                SetButtonMapping(layout, btn, GetDefaultButtonMapping(layout, btn));
        }

        /// <summary>Apakah Layered Mode aktif untuk layout ini? Default false.</summary>
        public static bool GetLayeredModeEnabled(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return false;
            var settings = ApplicationData.Current.LocalSettings;
            string key = $"LayeredEnabled_{layout}";
            if (settings.Values.TryGetValue(key, out object? value) && value is bool b) return b;
            return false;
        }

        /// <summary>Set Layered Mode aktif/tidak untuk layout, sync ke INI.</summary>
        public static void SetLayeredModeEnabled(string layout, bool enabled)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return;
            var settings = ApplicationData.Current.LocalSettings;
            string key = $"LayeredEnabled_{layout}";
            if (settings.Values.TryGetValue(key, out object? prev) && prev is bool pv && pv == enabled)
                return;
            settings.Values[key] = enabled;
            WriteShared($"LayeredMode.{layout}", "Enabled", enabled ? "1" : "0");
        }

        /// <summary>Tombol trigger untuk Layered Mode (default "RSPress").</summary>
        public static string GetLayeredModeButton(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return "RSPress";
            var settings = ApplicationData.Current.LocalSettings;
            string key = $"LayeredBtn_{layout}";
            if (settings.Values.TryGetValue(key, out object? value) && value is string s
                && Array.IndexOf(AllMappableButtons, s) >= 0) return s;
            return "RSPress";
        }

        /// <summary>Set tombol trigger Layered Mode, sync ke INI.</summary>
        public static void SetLayeredModeButton(string layout, string button)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) return;
            if (Array.IndexOf(AllMappableButtons, button) < 0) return;
            var settings = ApplicationData.Current.LocalSettings;
            string key = $"LayeredBtn_{layout}";
            if (settings.Values.TryGetValue(key, out object? prev) && prev is string pv && pv == button)
                return;
            settings.Values[key] = button;
            WriteShared($"LayeredMode.{layout}", "TriggerButton", button);
        }

        /// <summary>
        /// 偵測裝置是否內建廠商手把映射軟體（與 Mouse Mode 衝突需停用）。
        /// 目前清單僅包含 ROG Ally 家族（Armoury Crate SE）；未來可擴充其他掌機。
        /// HKLM 讀取不受 MSIX 虛擬化影響。
        /// 主程式、PhantomKey、PhantomLink 三處各自獨立偵測，不經 INI；
        /// 機型清單更新時必須三處同步修改：
        ///   - OmniConsole/Services/SettingsService.cs (此函式)
        ///   - OmniConsole.PhantomKey/Config.cpp (DetectBuiltInGamepadMapping)
        ///   - OmniConsole.PhantomLink/Services/HardwareDetection.cs (HasBuiltInGamepadMapping)
        /// </summary>
        public static bool HasBuiltInGamepadMapping()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (key?.GetValue("SystemProductName") is not string product) return false;
                var upper = product.ToUpperInvariant();
                string[] knownKeywords = { "RC71L", "RC72L", "RC72LA", "RC73XA", "RC73YA" };
                foreach (var kw in knownKeywords)
                    if (upper.Contains(kw)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>Export all button mappings + layered mode settings for both layouts to JSON.</summary>
        public static string ExportMappingJson()
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var layout in new[] { LayoutOmniNav, LayoutClassic })
            {
                var layoutObj = new System.Text.Json.Nodes.JsonObject();
                layoutObj["layeredEnabled"] = GetLayeredModeEnabled(layout);
                layoutObj["layeredButton"] = GetLayeredModeButton(layout);
                var mappings = new System.Text.Json.Nodes.JsonObject();
                foreach (var btn in AllMappableButtons)
                    mappings[btn] = GetButtonMapping(layout, btn);
                layoutObj["mappings"] = mappings;
                obj[layout] = layoutObj;
            }
            return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Export button mappings + layered mode settings for a single layout to JSON (layout-agnostic format).</summary>
        public static string ExportMappingJsonForLayout(string layout)
        {
            var obj = new System.Text.Json.Nodes.JsonObject();
            obj["layeredEnabled"] = GetLayeredModeEnabled(layout);
            obj["layeredButton"] = GetLayeredModeButton(layout);
            var mappings = new System.Text.Json.Nodes.JsonObject();
            foreach (var btn in AllMappableButtons)
                mappings[btn] = GetButtonMapping(layout, btn);
            obj["mappings"] = mappings;
            return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>Import button mappings + layered mode settings from JSON and persist.</summary>
        public static void ImportMappingJson(string json)
        {
            var obj = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject()
                ?? throw new System.Text.Json.JsonException("Invalid mapping JSON");
            foreach (var layout in new[] { LayoutOmniNav, LayoutClassic })
            {
                if (!obj.TryGetPropertyValue(layout, out var layoutNode) || layoutNode == null) continue;
                var layoutObj = layoutNode.AsObject();
                if (layoutObj.TryGetPropertyValue("layeredEnabled", out var le) && le != null)
                    SetLayeredModeEnabled(layout, le.GetValue<bool>());
                if (layoutObj.TryGetPropertyValue("layeredButton", out var lb) && lb != null)
                {
                    string btn = lb.GetValue<string>();
                    if (Array.IndexOf(AllMappableButtons, btn) >= 0)
                        SetLayeredModeButton(layout, btn);
                }
                if (layoutObj.TryGetPropertyValue("mappings", out var mNode) && mNode != null)
                {
                    var mappings = mNode.AsObject();
                    foreach (var btn in AllMappableButtons)
                    {
                        if (mappings.TryGetPropertyValue(btn, out var mv) && mv != null)
                            SetButtonMapping(layout, btn, mv.GetValue<string>());
                    }
                }
            }
        }

        /// <summary>Import button mappings + layered mode settings from JSON for a single layout and persist.
        /// Supports layout-agnostic format (flat) and legacy layout-keyed format.</summary>
        public static void ImportMappingJsonForLayout(string json, string targetLayout)
        {
            var obj = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject()
                ?? throw new System.Text.Json.JsonException("Invalid mapping JSON");

            // Determine the source object: flat format has "mappings" at root level,
            // legacy format wraps in a layout key (e.g. {"OmniNav": {...}} or {"Classic": {...}})
            System.Text.Json.Nodes.JsonObject? layoutObj;
            if (obj.ContainsKey("mappings"))
            {
                // Flat (layout-agnostic) format
                layoutObj = obj;
            }
            else
            {
                // Legacy layout-keyed format — try target layout first, then any layout key
                layoutObj = null;
                if (obj.TryGetPropertyValue(targetLayout, out var node) && node != null)
                    layoutObj = node.AsObject();
                else
                {
                    foreach (var layout in new[] { LayoutOmniNav, LayoutClassic })
                    {
                        if (obj.TryGetPropertyValue(layout, out var n) && n != null)
                        { layoutObj = n.AsObject(); break; }
                    }
                }
                if (layoutObj == null) throw new System.Text.Json.JsonException("No valid mapping data found in JSON");
            }

            if (layoutObj.TryGetPropertyValue("layeredEnabled", out var le) && le != null)
                SetLayeredModeEnabled(targetLayout, le.GetValue<bool>());
            if (layoutObj.TryGetPropertyValue("layeredButton", out var lb) && lb != null)
            {
                string btn = lb.GetValue<string>();
                if (Array.IndexOf(AllMappableButtons, btn) >= 0)
                    SetLayeredModeButton(targetLayout, btn);
            }
            if (layoutObj.TryGetPropertyValue("mappings", out var mNode) && mNode != null)
            {
                var mappings = mNode.AsObject();
                foreach (var btn in AllMappableButtons)
                {
                    if (mappings.TryGetPropertyValue(btn, out var mv) && mv != null)
                        SetButtonMapping(targetLayout, btn, mv.GetValue<string>());
                }
            }
        }
    }
}
