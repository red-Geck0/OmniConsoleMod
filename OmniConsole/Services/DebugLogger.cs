using System;
using System.IO;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 檔案式 Debug 日誌工具。由 Settings ＞ 進階 的「啟用除錯日誌」開關控制，預設關閉——
    /// 每次呼叫都是同步檔案 I/O，手把導覽等高頻路徑（EnsureFocus 等）每秒可能觸發數十次，
    /// 關閉時 <see cref="Log"/> 只讀一個已快取的布林值就返回，不碰設定存放區也不做檔案操作。
    /// 日誌位置：PublisherCacheFolder\OmniConsoleShared\DebugTrace.log
    ///（與 Shared.ini / GamepadProfiles.json 同目錄，即 %LOCALAPPDATA%\Publishers\&lt;PublisherHash&gt;\OmniConsoleShared\）。
    /// </summary>
    public static class DebugLogger
    {
        private const string SharedFolderName = "OmniConsoleShared";
        private const string LogFileName = "DebugTrace.log";

        private static string? _cachedPath;

        /// <summary>
        /// 開關快取。<see cref="Log"/> 每秒可能被手把導覽路徑呼叫上百次，而
        /// ApplicationData.Current.LocalSettings 每次存取都要跨 WinRT/COM 邊界——關閉狀態下
        /// 光是「問一下要不要記錄」就足以在連續導覽時造成可感知的頓挫。
        /// 值只在首次查詢時讀一次，之後由 <see cref="SetEnabled"/>（設定頁切換開關時）更新。
        /// </summary>
        private static bool? _enabled;

        /// <summary>
        /// 目前是否啟用記錄。呼叫端可先問這一句再組字串，
        /// 避免關閉時仍為了一行不會被寫出的訊息做字串內插與配置。
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                if (_enabled is bool cached) return cached;
                bool value;
                try { value = SettingsService.GetEnableDebugLogging(); }
                catch { value = false; }
                _enabled = value;
                return value;
            }
        }

        /// <summary>由 <see cref="SettingsService.SetEnableDebugLogging"/> 呼叫，讓開關即時生效。</summary>
        public static void SetEnabled(bool enabled) => _enabled = enabled;

        /// <summary>供「開啟記錄檔資料夾」按鈕使用，取得記錄檔所在資料夾路徑（不含檔名）。</summary>
        public static string? GetLogFolderPath()
        {
            var path = LogPath;
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        private static string LogPath
        {
            get
            {
                if (_cachedPath != null) return _cachedPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedPath = Path.Combine(folder.Path, LogFileName);
                }
                catch
                {
                    _cachedPath = string.Empty;
                }
                return _cachedPath;
            }
        }

        /// <summary>
        /// 寫入一行帶有時戳的日誌訊息。設定關閉時（預設）立即返回，不做任何檔案 I/O。
        /// </summary>
        public static void Log(string message)
        {
            if (!IsEnabled) return;
            try
            {
                var path = LogPath;
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }
    }
}
