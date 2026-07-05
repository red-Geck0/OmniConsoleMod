using System;
using System.IO;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 檔案式 Debug 日誌工具。由 Settings ＞ 進階 的「啟用除錯日誌」開關控制，預設關閉——
    /// 每次呼叫都是同步檔案 I/O，手把導覽等高頻路徑（EnsureFocus 等）每秒可能觸發數十次，
    /// 關閉時 <see cref="Log"/> 會立即返回、不做任何檔案操作，不影響操作反應速度。
    /// 日誌位置：PublisherCacheFolder\OmniConsoleShared\DebugTrace.log
    ///（與 Shared.ini / GamepadProfiles.json 同目錄，即 %LOCALAPPDATA%\Publishers\&lt;PublisherHash&gt;\OmniConsoleShared\）。
    /// </summary>
    public static class DebugLogger
    {
        private const string SharedFolderName = "OmniConsoleShared";
        private const string LogFileName = "DebugTrace.log";

        private static string? _cachedPath;

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
            if (!SettingsService.GetEnableDebugLogging()) return;
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
