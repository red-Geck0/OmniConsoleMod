using System;
using System.IO;
using Windows.Storage;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 檔案式 Debug 日誌工具。由 Settings ＞ Troubleshoot 的「啟用除錯日誌」開關控制（透過
    /// Shared.ini 的 [Debug] EnableLogging 讀取，見 PhantomKeyStore.GetEnableDebugLogging），
    /// 預設關閉時 <see cref="Log"/> 立即返回、不做任何檔案操作。
    /// 寫入與主程式 DebugLogger 相同的檔案，讓兩邊的紀錄依時間排序交錯，方便對照除錯：
    /// 日誌位置：PublisherCacheFolder\OmniConsoleShared\DebugTrace.log
    ///（與 Shared.ini / GamepadProfiles.json 同目錄，即 %LOCALAPPDATA%\Publishers\&lt;PublisherHash&gt;\OmniConsoleShared\）。
    /// </summary>
    internal static class DebugLogger
    {
        private const string SharedFolderName = "OmniConsoleShared";
        private const string LogFileName = "DebugTrace.log";

        private static string _cachedPath;

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

        public static void Log(string message)
        {
            if (!PhantomKeyStore.GetEnableDebugLogging()) return;
            try
            {
                var path = LogPath;
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }
    }
}
