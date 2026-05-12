using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 透過 PublisherCacheFolder 共用 INI 與主程式 / PhantomKey 交換設定。
    /// 同 Publisher (CN=8bit2qubit) 的 MSIX 套件共用資料夾，於沙箱規格內共用設定。
    /// </summary>
    internal static class PhantomKeyStore
    {
        // ── 常數與有效值 ─────────────────────────────────────────────────────

        public const string MouseModeOff = "Off";
        public const string MouseModeAuto = "Whitelist";
        public const string MouseModeForceOn = "Blacklist";

        public const string LayoutOmniNav = "OmniNav";
        public const string LayoutClassic = "Classic";

        // 預設平台 Id（與主程式 Models/PlatformCatalog.cs 一致）
        public const string PlatformSteamBigPicture = "SteamBigPicture";

        // Steam In-Game Overlay 預設快捷鍵；若 PhantomKey 尚未寫入 Shared.ini 則用此回退
        public const string DefaultSteamInGameOverlayShortcut = "Shift+Tab";

        public static readonly int[] ValidCursorSpeedPercents = { 25, 50, 75, 100, 125, 150, 175, 200 };

        // ── 共用 INI 路徑（PublisherCacheFolder） ────────────────────────────

        private const string SharedFolderName = "OmniConsoleShared";
        private const string SharedIniFileName = "Shared.ini";

        private static string _cachedPath;

        /// <summary>
        /// 同 Publisher MSIX 套件共用的 INI 檔路徑；首次讀取時建立資料夾並快取。
        /// </summary>
        private static string SharedIniPath
        {
            get
            {
                if (_cachedPath != null) return _cachedPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedPath = Path.Combine(folder.Path, SharedIniFileName);
                    DebugLogger.Log("[Store] PublisherCacheFolder=" + _cachedPath);
                }
                catch (Exception ex)
                {
                    _cachedPath = string.Empty;
                    DebugLogger.Log("[Store] GetPublisherCacheFolder FAIL: " + ex);
                }
                return _cachedPath;
            }
        }

        // ── INI 讀寫（Win32 Private Profile API） ───────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetPrivateProfileStringW(
            string lpAppName, string lpKeyName, string lpDefault,
            StringBuilder lpReturnedString, int nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileStringW(
            string lpAppName, string lpKeyName, string lpString, string lpFileName);

        /// <summary>
        /// 從共用 INI 讀值；路徑取不到或檔案不存在時回傳傳入的預設值。
        /// </summary>
        private static string Read(string section, string key, string def)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return def;
            var sb = new StringBuilder(256);
            GetPrivateProfileStringW(section, key, def, sb, sb.Capacity, path);
            return sb.ToString();
        }

        /// <summary>
        /// 寫入共用 INI；PublisherCacheFolder 無法取得時靜默略過。
        /// </summary>
        private static void Write(string section, string key, string value)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path)) return;
            try { WritePrivateProfileStringW(section, key, value, path); } catch { }
        }

        /// <summary>
        /// Shared.ini 不存在時一次補齊 [PhantomKey] 所有預設值，避免只寫單一 key
        /// 造成其他 key 首次讀取拿不到值。[General] DefaultPlatform 屬主程式職責，不在此處寫入。
        /// </summary>
        public static void EnsureDefaultsIfMissing()
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return;
            Write("PhantomKey", "MouseMode", MouseModeAuto);  // "Whitelist"
            Write("PhantomKey", "MouseModeLayout", LayoutOmniNav);
            Write("PhantomKey", "CursorSpeedPercent", "100");
            Write("PhantomKey", "SteamInGameOverlayEnabled", "1");
            DebugLogger.Log("[Store] Seeded default [PhantomKey] values.");
        }

        // ── 公開 API：Mouse Mode ─────────────────────────────────────────────

        /// <summary>
        /// 讀取 Mouse Mode（Off / Whitelist / Blacklist）；無效或缺失值回 Whitelist。
        /// 相容舊版 "Auto" → "Whitelist"、"ForceOn" → "Blacklist"。
        /// </summary>
        public static string GetMouseMode()
        {
            var s = Read("PhantomKey", "MouseMode", MouseModeAuto);
            if (s == MouseModeOff || s == MouseModeAuto || s == MouseModeForceOn) return s;
            // 相容舊版值
            if (s == "Auto") return MouseModeAuto;
            if (s == "ForceOn") return MouseModeForceOn;
            return MouseModeAuto;
        }

        /// <summary>
        /// 寫入 Mouse Mode；非預期值會回退成 Whitelist，避免下次讀取再做一次回退。
        /// </summary>
        public static void SetMouseMode(string mode)
        {
            if (mode != MouseModeOff && mode != MouseModeAuto && mode != MouseModeForceOn)
                mode = MouseModeAuto;
            Write("PhantomKey", "MouseMode", mode);
        }

        // ── 公開 API：Layout ─────────────────────────────────────────────────

        /// <summary>
        /// 讀取手把配置（OmniNav / Classic）；無效或缺失值回 OmniNav。
        /// </summary>
        public static string GetMouseModeLayout()
        {
            var s = Read("PhantomKey", "MouseModeLayout", LayoutOmniNav);
            if (s == LayoutClassic) return LayoutClassic;
            return LayoutOmniNav;
        }

        /// <summary>
        /// 寫入手把配置；非預期值回退成 OmniNav。
        /// </summary>
        public static void SetMouseModeLayout(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) layout = LayoutOmniNav;
            Write("PhantomKey", "MouseModeLayout", layout);
        }

        // ── 公開 API：Steam In-Game Overlay ──────────────────────────────────

        /// <summary>
        /// 讀取長按 ☰ 觸發 Steam In-Game Overlay 是否啟用；與主程式 WriteShared 約定一致（"1"=啟用）。
        /// 非 "0" 一律視為啟用，與缺失情境的預設值對齊。
        /// </summary>
        public static bool GetSteamInGameOverlayEnabled()
        {
            var s = Read("PhantomKey", "SteamInGameOverlayEnabled", "1");
            return s != "0";
        }

        /// <summary>
        /// 寫入長按 ☰ 觸發 Steam In-Game Overlay 開關狀態。
        /// </summary>
        public static void SetSteamInGameOverlayEnabled(bool enabled)
        {
            Write("PhantomKey", "SteamInGameOverlayEnabled", enabled ? "1" : "0");
        }

        // ── 公開 API：Cursor Speed ──────────────────────────────────────────

        /// <summary>
        /// 讀取游標速度百分比；必須落在 ValidCursorSpeedPercents 範圍內，否則回 100。
        /// </summary>
        public static int GetCursorSpeedPercent()
        {
            var s = Read("PhantomKey", "CursorSpeedPercent", "100");
            if (int.TryParse(s, out int pct))
            {
                foreach (var p in ValidCursorSpeedPercents)
                    if (p == pct) return p;
            }
            return 100;
        }

        /// <summary>
        /// 寫入游標速度百分比；不在 ValidCursorSpeedPercents 範圍時回退成 100。
        /// </summary>
        public static void SetCursorSpeedPercent(int percent)
        {
            int valid = 100;
            foreach (var p in ValidCursorSpeedPercents)
                if (p == percent) { valid = p; break; }
            Write("PhantomKey", "CursorSpeedPercent", valid.ToString());
        }

        // ── 公開 API：DefaultPlatform / SteamInGameOverlay 快捷鍵 ───────────

        /// <summary>
        /// 讀取目前預設平台 Id（[General] DefaultPlatform）。值由主程式於 SettingsService 寫入；
        /// Widget 用於決定平台特定按鈕的可見性（例如 SteamInGameOverlayBtn 僅在 SteamBigPicture 顯示）。
        /// </summary>
        public static string GetDefaultPlatform()
        {
            return Read("General", "DefaultPlatform", string.Empty);
        }

        /// <summary>
        /// 讀取 Steam In-Game Overlay 快捷鍵字串（如 "Shift+Tab"、"Insert"）。
        /// 來源：PhantomKey 解析 Steam VDF 後寫入 [PhantomKey] SteamInGameOverlayShortcut（cached）。
        /// 若 PhantomKey 尚未執行（FSE 尚未啟動平台）或 Steam 未安裝，回傳預設 "Shift+Tab"。
        /// </summary>
        public static string GetSteamInGameOverlayShortcut()
        {
            var s = Read("PhantomKey", "SteamInGameOverlayShortcut", DefaultSteamInGameOverlayShortcut);
            return string.IsNullOrEmpty(s) ? DefaultSteamInGameOverlayShortcut : s;
        }

        // ── 公開 API：Foreground Process ─────────────────────────────────────

        /// <summary>
        /// 讀取 PhantomKey 寫入的目前前景程式名（不含 .exe）。
        /// 若 PhantomKey 尚未執行或尚未寫入，回傳空字串。
        /// </summary>
        public static string GetForegroundProcess()
        {
            return Read("PhantomKey", "ForegroundProcess", string.Empty);
        }

        // ── 公開 API：Whitelist / Blacklist 操作 ─────────────────────────────

        /// <summary>
        /// 讀取白名單應用程式清單（CSV 格式，逗號分隔）。
        /// </summary>
        public static string[] GetWhitelist()
        {
            var csv = Read("MouseMode.Whitelist", "Apps", string.Empty);
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 寫入白名單應用程式清單。
        /// </summary>
        public static void SetWhitelist(string[] apps)
        {
            var csv = string.Join(",", apps);
            Write("MouseMode.Whitelist", "Apps", csv);
        }

        /// <summary>
        /// 讀取黑名單應用程式清單（CSV 格式，逗號分隔）。
        /// </summary>
        public static string[] GetBlacklist()
        {
            var csv = Read("MouseMode.Blacklist", "Apps", string.Empty);
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 寫入黑名單應用程式清單。
        /// </summary>
        public static void SetBlacklist(string[] apps)
        {
            var csv = string.Join(",", apps);
            Write("MouseMode.Blacklist", "Apps", csv);
        }
    }
}
