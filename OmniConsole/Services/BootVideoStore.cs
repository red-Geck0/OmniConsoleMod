using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理使用者自選開機影片檔案的複製與清除。
    /// 影片儲存於 LocalFolder/BootVideo/，只保留一份（每次匯入取代舊檔），
    /// 避免直接讀取外部路徑可能遇到的封裝應用程式檔案存取限制。
    /// </summary>
    public static class BootVideoStore
    {
        private const string FolderName = "BootVideo";

        /// <summary>
        /// 將使用者選取的影片檔複製到 LocalFolder/BootVideo/，取代先前匯入的檔案。
        /// 保留原始副檔名（播放器需要正確的容器格式資訊），實際存檔用亂數檔名避免路徑碰撞；
        /// 原始檔名另外存進 SettingsService.SetBootVideoDisplayName，只給設定頁顯示用。
        /// 回傳儲存後的檔名。
        /// </summary>
        public static async Task<string> ImportVideoAsync(StorageFile sourceFile)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var videoFolder = await localFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

            // 先清掉資料夾內舊檔案，確保只保留目前這一份（開機影片一次僅一支，非清單）
            foreach (var existing in await videoFolder.GetFilesAsync())
            {
                try { await existing.DeleteAsync(); } catch { }
            }

            string ext = Path.GetExtension(sourceFile.Name);
            string fileName = $"{Guid.NewGuid():N}{ext}";
            await sourceFile.CopyAsync(videoFolder, fileName, NameCollisionOption.ReplaceExisting);
            SettingsService.SetBootVideoDisplayName(sourceFile.Name);
            return fileName;
        }

        /// <summary>
        /// 取得目前已匯入之開機影片的完整路徑；未匯入或檔案不存在時回 null。
        /// </summary>
        public static string? GetVideoFilePath()
        {
            string fileName = SettingsService.GetBootVideoFileName();
            if (string.IsNullOrEmpty(fileName)) return null;

            string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName, fileName);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// 刪除目前已匯入的開機影片檔案並清除設定。
        /// </summary>
        public static void ClearVideo()
        {
            try
            {
                string folderPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);
                if (Directory.Exists(folderPath))
                {
                    foreach (var file in Directory.GetFiles(folderPath))
                        File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[BootVideoStore] ClearVideo failed: {ex.Message}");
            }
            SettingsService.SetBootVideoFileName(string.Empty);
            SettingsService.SetBootVideoDisplayName(string.Empty);
        }
    }
}
