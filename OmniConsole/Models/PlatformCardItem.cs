using Microsoft.UI.Xaml;

namespace OmniConsole.Models
{
    /// <summary>
    /// 設定介面平台選擇卡片的資料模型。
    /// 刻意不實作 INotifyPropertyChanged，避免 Release 模式 IL Trimming 修剪事件訂閱基礎設施。
    /// IsAvailable 更新後需由外部重新指定 ItemsSource 來重新整理 OneTime 繫結。
    /// </summary>
    public class PlatformCardItem
    {
        /// <summary>對應的平台定義資料。</summary>
        public required PlatformDefinition Platform { get; init; }

        /// <summary>便捷存取平台 Id。</summary>
        public string Id => Platform.Id;

        /// <summary>便捷存取平台圖示路徑（ms-appx:///Assets/Platforms/xxx.png）。</summary>
        public string IconAsset => Platform.IconAsset;

        /// <summary>UI 顯示用的在地化名稱（由外部設定）。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>此平台是否已安裝於目前裝置上。</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>
        /// 卡片透明度：已安裝為 1.0，未安裝為 0.2（視覺上呈現停用感）。
        /// </summary>
        public double CardOpacity => IsAvailable ? 1.0 : 0.2;

        /// <summary>
        /// 是否為使用者自訂平台（相對於系統內建平台）。系統/使用者平台合併於單一卡片網格後，
        /// 右鍵匯出選單、X 編輯提示等原本「僅使用者索引標籤可用」的行為改依此旗標逐卡判定。
        /// </summary>
        public bool IsCustom { get; init; }

        /// <summary>
        /// 是否為卡片網格尾端固定的「新增自訂平台」動作卡（非真實平台，僅供觸發新增流程）。
        /// </summary>
        public bool IsAddNewCard { get; init; }

        /// <summary>
        /// 此卡片只用來指定 Windows 的 FSE Home App，本身不是 OmniConsole 可啟動的平台。
        /// 涵蓋「無」、「Xbox App（Windows 原生）」與掃描到的其它 FSE Shell。
        /// 選取這類卡片時只改寫 Home App，不覆寫使用者已選好的預設啟動平台——
        /// Home App 一旦不是 OmniConsole，本應用程式根本不會被叫起來，
        /// 硬把它記成「預設平台」只會在使用者換回來時發現原本的選擇已經被洗掉。
        /// </summary>
        public bool IsHomeAppOnly { get; init; }

        /// <summary>
        /// 是否為「無 Home App」卡片。沒有對應的圖示資產，改以字形圖示呈現，
        /// 也不參與可用性查詢（它永遠可選）。
        /// </summary>
        public bool IsNoneCard { get; init; }

        /// <summary>一般平台卡片內容（圖示 + 名稱）的可見度；動作卡與「無」卡片時隱藏。</summary>
        public Visibility NormalCardVisibility =>
            IsAddNewCard || IsNoneCard ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>「新增自訂平台」動作卡內容的可見度；一般平台卡片時隱藏。</summary>
        public Visibility AddCardVisibility => IsAddNewCard ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>「無 Home App」卡片內容的可見度；其它卡片時隱藏。</summary>
        public Visibility NoneCardVisibility => IsNoneCard ? Visibility.Visible : Visibility.Collapsed;
    }
}
