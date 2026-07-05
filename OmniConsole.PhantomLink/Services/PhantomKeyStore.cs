using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 透過 PublisherCacheFolder 共用 INI 與主程式 / PhantomKey 交換設定。
    /// 同 Publisher (CN=red-Geck0) 的 MSIX 套件共用資料夾，於沙箱規格內共用設定。
    /// </summary>
    internal static class PhantomKeyStore
    {
        // ── 常數與有效值 ─────────────────────────────────────────────────────

        public const string MouseModeOff = "Off";
        public const string MouseModeOn = "On";

        // 預設平台 Id（與主程式 Models/PlatformCatalog.cs 一致）
        public const string PlatformSteamBigPicture = "SteamBigPicture";

        // Steam In-Game Overlay 預設快捷鍵；若 PhantomKey 尚未寫入 Shared.ini 則用此回退
        public const string DefaultSteamInGameOverlayShortcut = "Shift+Tab";

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
            Write("PhantomKey", "MouseMode", MouseModeOn);
            Write("PhantomKey", "SteamInGameOverlayEnabled", "1");
            DebugLogger.Log("[Store] Seeded default [PhantomKey] values.");
        }

        // ── 公開 API：Mouse Mode ─────────────────────────────────────────────

        /// <summary>讀取 Mouse Mode 是否啟用；"Off" 為停用，其餘（含舊值 Auto/ForceOn）視為啟用。</summary>
        public static bool GetMouseModeEnabled()
        {
            var s = Read("PhantomKey", "MouseMode", MouseModeOn);
            return s != MouseModeOff;
        }

        /// <summary>寫入 Mouse Mode On/Off。</summary>
        public static void SetMouseModeEnabled(bool enabled)
        {
            Write("PhantomKey", "MouseMode", enabled ? MouseModeOn : MouseModeOff);
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

        // ── 公開 API：手把映射 profile 清單 ─────────────────────────────────

        /// <summary>
        /// 讀取手把映射 profile 清單（id + 名稱 + 是否唯讀）。
        /// 來源：PhantomKey 解析 GamepadProfiles.json 後寫入 [Profiles] Count / IdN / NameN / ReadOnlyN。
        /// 缺失或解析失敗回空清單。
        /// </summary>
        public static List<(string Id, string Name, bool IsReadOnly)> GetProfileList()
        {
            var result = new List<(string, string, bool)>();
            var s = Read("Profiles", "Count", "0");
            if (!int.TryParse(s, out int count) || count <= 0) return result;
            if (count > 256) count = 256;  // 防呆上限
            for (int i = 0; i < count; i++)
            {
                string id = Read("Profiles", "Id" + i, string.Empty);
                if (string.IsNullOrEmpty(id)) continue;
                string name = Read("Profiles", "Name" + i, id);
                bool isReadOnly = Read("Profiles", "ReadOnly" + i, "0") == "1";
                result.Add((id, name, isReadOnly));
            }
            return result;
        }

        /// <summary>
        /// 讀取預設 profile id（[Profiles] DefaultId）。
        /// PhantomKey 在 WriteProfileList 時同步寫入；缺失回空字串。
        /// </summary>
        public static string GetDefaultProfileId()
            => Read("Profiles", "DefaultId", string.Empty);

        // ── 公開 API：Widget 狀態 / Active profile ───────────────────────────

        /// <summary>
        /// 通知 PhantomKey Widget 目前是否浮現（Game Bar 開啟中）。
        /// WidgetActive=1 時 PhantomKey 會暫停 Mouse Mode，讓 Game Bar 原生手把 UI 正常運作。
        /// </summary>
        public static void SetWidgetActive(bool active)
            => Write("Status", "WidgetActive", active ? "1" : "0");

        /// <summary>
        /// 讀取 PhantomKey 最後寫入的 active profile id（[Status] ActiveProfileId）。
        /// 記錄的是「最後一個非 Widget 前景所套用的 profile」，供 Widget 在填下拉時預選。
        /// 若尚未寫入（PhantomKey 從未執行過）回空字串。
        /// </summary>
        public static string GetActiveProfileId()
            => Read("Status", "ActiveProfileId", string.Empty);

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
    }
}
