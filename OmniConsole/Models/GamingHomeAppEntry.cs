using System.Text.Json.Serialization;

namespace OmniConsole.Models
{
    /// <summary>
    /// 一個「可被 Windows 選為 FSE Home App」的已安裝應用程式的可序列化快取資料。
    ///
    /// 對應的是宣告了 windows.gamingApp 延伸模組的套件，也就是
    /// 設定 ＞ 遊戲 ＞ 全螢幕體驗 ＞ 選擇主畫面應用程式 清單裡會出現的那些項目
    /// （AnyFSE 之類與本應用程式功能重疊的第三方 Shell 即屬此類）。
    /// 掃描由 <c>GamingHomeAppStore</c> 負責，結果快取成 JSON 後才轉為平台定義，
    /// 讓 FindById 這類同步呼叫在掃描尚未完成時也有資料可用。
    /// </summary>
    public class GamingHomeAppEntry
    {
        /// <summary>平台 Id 的前綴，用來與內建平台、使用者自訂平台區分。</summary>
        public const string IdPrefix = "gamingapp_";

        /// <summary>DisplayNameKey 的前綴；非 .resw 資源鍵，顯示名稱改由本快取提供。</summary>
        public const string DisplayNameKeyPrefix = "__gamingapp__";

        /// <summary>擷取出的套件圖示存放資料夾（位於 LocalFolder 下）。</summary>
        public const string IconFolderName = "GamingHomeAppIcons";

        /// <summary>套件家族名稱，同時作為此項目的唯一鍵（例如 "ArtemShpynov.AnyFSE_xxxxxxxxxxxxx"）。</summary>
        [JsonPropertyName("packageFamilyName")]
        public string PackageFamilyName { get; set; } = "";

        /// <summary>
        /// 此 App 的 AUMID（家族名稱 + "!" + Application Id）。
        /// 這正是 GamingHomeApp 登錄值要寫的內容，用來把 Home App 交還給這個 App。
        /// </summary>
        [JsonPropertyName("aumid")]
        public string Aumid { get; set; } = "";

        /// <summary>由套件 manifest 取得的在地化顯示名稱。</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        /// <summary>擷取出的套件圖示檔名（位於 LocalFolder/GamingHomeAppIcons/）；取不到時為空字串。</summary>
        [JsonPropertyName("iconFileName")]
        public string IconFileName { get; set; } = "";

        /// <summary>穩定的平台 Id，由套件家族名稱衍生，重新掃描後不會改變。</summary>
        [JsonIgnore]
        public string Id => IdPrefix + PackageFamilyName;

        /// <summary>
        /// 轉換為引擎可用的 <see cref="PlatformDefinition"/>。
        /// 一律以 PackagedApp 策略啟動：這些項目本來就是已安裝的封裝應用程式，
        /// 家族名稱是掃描當下直接讀到的，不需要再猜測 URI 或執行檔路徑。
        /// </summary>
        public PlatformDefinition ToPlatformDefinition()
        {
            var strategy = new LaunchStrategy
            {
                Type = LaunchStrategyType.PackagedApp,
                PackageFamilyName = PackageFamilyName,
            };

            string iconAsset = string.IsNullOrEmpty(IconFileName)
                ? "ms-appx:///Assets/Platforms/custom.png"
                : $"ms-appdata:///local/{IconFolderName}/{IconFileName}";

            return new PlatformDefinition
            {
                Id = Id,
                DisplayNameKey = DisplayNameKeyPrefix + Id,
                IconAsset = iconAsset,
                AvailabilityStrategy = strategy,
                LaunchStrategies = [strategy],
            };
        }
    }

    /// <summary>
    /// System.Text.Json 原始碼產生器上下文，於編譯期產生序列化程式碼，
    /// 不依賴執行期反射，確保 IL Trimming 下正常運作。
    /// </summary>
    [JsonSerializable(typeof(GamingHomeAppEntry[]))]
    internal partial class GamingHomeAppJsonContext : JsonSerializerContext
    {
    }
}
