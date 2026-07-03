using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 自訂平台功能免責聲明對話方塊：Primary=接受、Close=取消。
    /// 於使用者首次嘗試新增或匯入自訂平台時顯示（見 SettingsPage.TryAddCustomPlatformAsync /
    /// ImportPlatformButton_Click），取代舊版「進入使用者索引標籤即整頁替換為聲明」的做法。
    /// 自帶手把輪詢（A=觸發焦點鈕、B=取消），與 SetDefaultScopeDialog 一致。
    /// </summary>
    public sealed partial class CustomPlatformConsentDialog : ContentDialog
    {
        private GamepadNavigationService? _gamepadNav;

        /// <summary>使用者是否點選「我了解並接受」；取消／B 鍵為 false。</summary>
        public bool Accepted { get; private set; }

        public CustomPlatformConsentDialog(XamlRoot xamlRoot, ResourceLoader resourceLoader)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            Title = resourceLoader.GetString("CustomPlatform_ConsentTitle");
            PrimaryButtonText = resourceLoader.GetString("CustomPlatform_ConsentAccept");
            CloseButtonText = resourceLoader.GetString("PlatformDialog_Cancel");

            PrimaryButtonClick += (s, e) => Accepted = true;

            Opened += OnOpened;
            Closed += OnClosed;
        }

        /// <summary>對話方塊開啟：啟動自帶手把輪詢（B=取消、A=觸發焦點元素）。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: () => Hide());
            _gamepadNav.Start();
        }

        /// <summary>對話方塊關閉：停止手把輪詢並釋放。</summary>
        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
        }
    }
}
