using OmniConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppExtensions;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 掃描系統上所有「可被選為 FSE Home App」的應用程式，並將它們提供為可選的預設平台。
    ///
    /// 動機：AnyFSE 之類與本應用程式功能重疊的第三方 Shell 也會登記成 Home App 候選。
    /// 使用者把 Home App 交給 OmniConsole 之後，那些 Shell 就再也進不去了——除非我們也把
    /// 它們列進平台清單，讓使用者能指定「由 OmniConsole 開機，再接力啟動它」。
    /// 因此這裡的來源刻意跟 Windows 設定介面同一份：宣告 windows.gamingApp 延伸模組的套件。
    ///
    /// 掃描權限：AppExtensionCatalog 要求呼叫端「MediumIL 以上」或「AppContainer + 對應的
    /// appExtensionHost 宣告 / packageQuery 能力」。本應用程式宣告 runFullTrust，屬於前者，
    /// 因此不需要在 Package.appxmanifest 額外宣告 host，也不需要 packageQuery。
    ///
    /// 快取：掃描是非同步的，但 FindById／GetAllDefinitions 會被啟動流程同步呼叫，
    /// 所以掃描結果先寫入 LocalFolder/GamingHomeApps.json，同步查詢一律讀這份快取
    /// （做法與 <see cref="UserPlatformStore"/> 一致）。
    /// </summary>
    public static class GamingHomeAppStore
    {
        /// <summary>Windows 用來辨識 Home App 候選的延伸模組名稱。</summary>
        private const string ExtensionContractName = "windows.gamingApp";

        private const string FileName = "GamingHomeApps.json";

        /// <summary>「無 Home App」卡片的 Id。選它等於 Windows 設定介面選「無」，系統將無法進入 FSE。</summary>
        public const string NoneId = "homeapp_none";

        /// <summary>「Xbox App（Windows 原生）」卡片的 Id。選它等於把 Home App 交給 Xbox。</summary>
        public const string NativeXboxId = "homeapp_xbox";

        /// <summary>
        /// Xbox 作為 Home App 時的 AUMID。
        /// 注意與 PlatformCatalog 的 XboxApp 平台意義不同：那張卡是「OmniConsole 仍是 Shell，
        /// 由它去啟動 Xbox App」；這裡則是「Xbox 自己接手當 Shell，OmniConsole 不會被叫起來」。
        /// </summary>
        public const string NativeXboxAumid = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

        private static List<GamingHomeAppEntry> _entries = [];
        private static bool _loaded;

        /// <summary>「無 Home App」卡片的平台定義。啟動策略僅為佔位，永遠不會被執行。</summary>
        public static PlatformDefinition CreateNoneDefinition() => new()
        {
            Id = NoneId,
            DisplayNameKey = "Platform_HomeAppNone",
            // 卡片以字形圖示呈現（見 PlatformCardItem.IsNoneCard），此值不會被顯示，
            // 但仍須為合法 URI，否則 x:Bind 套用到 Image.Source 時會拋 UriFormatException。
            IconAsset = "ms-appx:///Assets/Platforms/custom.png",
            AvailabilityStrategy = new LaunchStrategy { Type = LaunchStrategyType.Executable },
            LaunchStrategies = [],
        };

        /// <summary>「Xbox App（Windows 原生）」卡片的平台定義。</summary>
        public static PlatformDefinition CreateNativeXboxDefinition()
        {
            var strategy = new LaunchStrategy
            {
                Type = LaunchStrategyType.PackagedApp,
                PackageFamilyName = "Microsoft.GamingApp_8wekyb3d8bbwe",
            };
            return new PlatformDefinition
            {
                Id = NativeXboxId,
                DisplayNameKey = "Platform_XboxAppNative",
                IconAsset = "ms-appx:///Assets/Platforms/xbox.png",
                // 借用 PackagedApp 策略做可用性判斷：Xbox 沒安裝就把卡片打暗，
                // 免得使用者交出 Home App 後反而進不了任何 FSE。
                AvailabilityStrategy = strategy,
                LaunchStrategies = [],
            };
        }

        /// <summary>
        /// 依卡片 Id 套用對應的 Home App 設定。
        /// </summary>
        /// <returns>已套用為 true；Id 不屬於 Home App 卡片或寫入失敗為 false。</returns>
        public static bool ApplyHomeAppSelection(string id)
        {
            if (id == NoneId) return FseService.TryClearHomeApp();
            if (id == NativeXboxId) return FseService.TrySetHomeApp(NativeXboxAumid);

            var entry = FindEntryById(id);
            return entry is not null && FseService.TrySetHomeApp(entry.Aumid);
        }

        /// <summary>
        /// 反查 Windows 目前的 Home App 對應到哪一張卡片。
        /// Home App 是 OmniConsole 自己（或無法辨識的 App）時回傳空字串，
        /// 表示「不由 Home App 決定選取」，改由使用者存檔的預設平台決定。
        /// </summary>
        public static string ResolveSelectedIdFromHomeApp()
        {
            string aumid = FseService.GetHomeAppAumid();

            if (aumid.Length == 0) return NoneId;
            if (aumid.Equals(NativeXboxAumid, StringComparison.OrdinalIgnoreCase)) return NativeXboxId;

            return FindEntryByAumid(aumid)?.Id ?? "";
        }

        /// <summary>
        /// 取得所有已掃描到的 Home App 候選（轉換為 PlatformDefinition）。
        /// </summary>
        public static IReadOnlyList<PlatformDefinition> GetAllDefinitions()
        {
            EnsureLoaded();
            return _entries.Select(e => e.ToPlatformDefinition()).ToList();
        }

        /// <summary>
        /// 以 Id 查找 Home App 候選的平台定義。
        /// </summary>
        public static PlatformDefinition? FindById(string id)
        {
            EnsureLoaded();
            return _entries.FirstOrDefault(e => e.Id == id)?.ToPlatformDefinition();
        }

        /// <summary>
        /// 以 Id 查找 Home App 候選的原始資料（顯示名稱、AUMID 等）。
        /// </summary>
        public static GamingHomeAppEntry? FindEntryById(string id)
        {
            EnsureLoaded();
            return _entries.FirstOrDefault(e => e.Id == id);
        }

        /// <summary>
        /// 以 AUMID 查找 Home App 候選，供「Windows 目前選的 Home App 是哪一個」的反查使用。
        /// AUMID 為空字串時一律回 null，避免尚未成功解析 AUMID 的項目被誤配對。
        /// </summary>
        public static GamingHomeAppEntry? FindEntryByAumid(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            EnsureLoaded();
            return _entries.FirstOrDefault(e =>
                !string.IsNullOrEmpty(e.Aumid) &&
                e.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 重新掃描系統上的 Home App 候選並更新快取。
        /// 排除本應用程式自己，以及已由 <see cref="PlatformCatalog"/> 內建收錄的套件
        /// （Xbox 即屬此類——內建項目有專屬圖示與多段啟動策略，比這裡掃出來的通用項目完整）。
        /// 失敗時保留既有快取，不清空：寧可沿用上次的結果，也不要讓使用者已選好的平台憑空消失。
        /// </summary>
        public static async Task RefreshAsync()
        {
            EnsureLoaded();

            try
            {
                var catalog = AppExtensionCatalog.Open(ExtensionContractName);
                var extensions = await catalog.FindAllAsync();

                string ownFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                var builtInFamilyNames = CollectBuiltInPackageFamilyNames();

                var found = new List<GamingHomeAppEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var extension in extensions)
                {
                    string familyName = extension.Package.Id.FamilyName;

                    if (familyName.Equals(ownFamilyName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (builtInFamilyNames.Contains(familyName)) continue;
                    // 同一套件可宣告多個延伸模組，只保留一筆
                    if (!seen.Add(familyName)) continue;

                    var entry = new GamingHomeAppEntry
                    {
                        PackageFamilyName = familyName,
                        DisplayName = ResolveDisplayName(extension, familyName),
                        Aumid = ResolveAumid(extension, familyName),
                    };
                    entry.IconFileName = await TryExportLogoAsync(extension, familyName);
                    found.Add(entry);

                    DebugLogger.Log($"[GamingHomeAppStore] Found: {entry.DisplayName} ({familyName})");
                }

                // 已解除安裝的項目：一併清掉擷取出來的圖示檔，避免 LocalFolder 越積越多
                foreach (var stale in _entries.Where(old =>
                    !found.Any(n => n.PackageFamilyName.Equals(old.PackageFamilyName, StringComparison.OrdinalIgnoreCase))))
                {
                    DeleteIconFile(stale.IconFileName);
                }

                _entries = found;
                Save();
                DebugLogger.Log($"[GamingHomeAppStore] Refresh done: {found.Count} entrie(s).");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Refresh failed: {ex.Message}");
            }
        }

        // ── 內部方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 收集 <see cref="PlatformCatalog"/> 內建平台已使用的所有套件家族名稱。
        /// 掃描結果與其中任一項重複時略過，避免同一個 App 在清單裡出現兩張卡片。
        /// </summary>
        private static HashSet<string> CollectBuiltInPackageFamilyNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in PlatformCatalog.All)
            {
                if (!string.IsNullOrEmpty(platform.AvailabilityStrategy.PackageFamilyName))
                    names.Add(platform.AvailabilityStrategy.PackageFamilyName!);

                foreach (var strategy in platform.LaunchStrategies)
                {
                    if (!string.IsNullOrEmpty(strategy.PackageFamilyName))
                        names.Add(strategy.PackageFamilyName!);
                }
            }
            return names;
        }

        /// <summary>
        /// 取得顯示名稱。優先用 AppInfo 的在地化名稱（等同開始功能表上顯示的名稱），
        /// 其次退回套件層級的 DisplayName，最後退回套件家族名稱。
        /// 延伸模組自身的 DisplayName 不列入：它可能是尚未解析的 ms-resource: 字串。
        /// </summary>
        private static string ResolveDisplayName(AppExtension extension, string familyName)
        {
            try
            {
                string? name = extension.AppInfo?.DisplayInfo?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] AppInfo display name failed for {familyName}: {ex.Message}");
            }

            try
            {
                string name = extension.Package.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Package display name failed for {familyName}: {ex.Message}");
            }

            return familyName;
        }

        /// <summary>
        /// 取得此延伸模組所屬 App 的 AUMID。取不到時退回「家族名稱!App」——Home App 候選幾乎
        /// 都把進入點命名為 App（本應用程式與 AnyFSE 皆是），至少讓反查有機會命中；
        /// 真的不符時只是配對不到，不會誤寫成別的 App。
        /// </summary>
        private static string ResolveAumid(AppExtension extension, string familyName)
        {
            try
            {
                string? aumid = extension.AppInfo?.AppUserModelId;
                if (!string.IsNullOrWhiteSpace(aumid)) return aumid!;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] AUMID lookup failed for {familyName}: {ex.Message}");
            }
            return $"{familyName}!App";
        }

        /// <summary>
        /// 將套件圖示擷取到 LocalFolder/GamingHomeAppIcons/，回傳檔名；失敗時回傳空字串
        /// （呼叫端會退回通用的自訂平台圖示）。檔名直接用套件家族名稱：其字元集本來就限於
        /// 英數與 .-_ ，可安全作為檔名，且重新掃描時會覆寫同一個檔案而非不斷產生新檔。
        /// </summary>
        private static async Task<string> TryExportLogoAsync(AppExtension extension, string familyName)
        {
            try
            {
                var logo = extension.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(256, 256));
                if (logo is null) return "";

                var iconFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    GamingHomeAppEntry.IconFolderName, CreationCollisionOption.OpenIfExists);

                string fileName = $"{familyName}.png";
                var destFile = await iconFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                using (var source = await logo.OpenReadAsync())
                using (var dest = await destFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    await Windows.Storage.Streams.RandomAccessStream.CopyAsync(source, dest);
                    await dest.FlushAsync();
                }

                return fileName;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Export logo failed for {familyName}: {ex.Message}");
                return "";
            }
        }

        /// <summary>刪除 LocalFolder/GamingHomeAppIcons/ 中的指定圖示檔案。</summary>
        private static void DeleteIconFile(string iconFileName)
        {
            if (string.IsNullOrEmpty(iconFileName)) return;
            try
            {
                string iconPath = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, GamingHomeAppEntry.IconFolderName, iconFileName);
                if (File.Exists(iconPath))
                    File.Delete(iconPath);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Delete icon failed: {ex.Message}");
            }
        }

        /// <summary>延遲初始化：首次呼叫時從 LocalFolder 讀取 JSON 快取；後續呼叫直接返回。</summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                string filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var entries = JsonSerializer.Deserialize(json, GamingHomeAppJsonContext.Default.GamingHomeAppEntryArray);
                    _entries = entries?.ToList() ?? [];
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Load failed: {ex.Message}");
                _entries = [];
            }
        }

        /// <summary>將目前快取序列化為 JSON 並寫入 LocalFolder；失敗時僅記錄，不拋例外。</summary>
        private static void Save()
        {
            try
            {
                string filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                string json = JsonSerializer.Serialize(_entries.ToArray(), GamingHomeAppJsonContext.Default.GamingHomeAppEntryArray);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamingHomeAppStore] Save failed: {ex.Message}");
            }
        }
    }
}
