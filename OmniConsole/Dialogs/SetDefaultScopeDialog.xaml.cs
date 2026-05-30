using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>使用者把某 profile 設為預設時，選擇套用範圍：一般 App 預設 / 遊戲預設 / 取消。</summary>
    public enum DefaultScope { None, Apps, Games }

    /// <summary>
    /// 「設為預設」範圍選擇對話方塊：Primary=App 預設、Secondary=遊戲預設、Close=取消。
    /// 自帶手把輪詢（A=觸發焦點鈕、B=取消），與 GamepadMessageDialog 一致。
    /// </summary>
    public sealed partial class SetDefaultScopeDialog : ContentDialog
    {
        private GamepadNavigationService? _gamepadNav;

        /// <summary>使用者選擇的範圍；未選（取消／B）為 None。</summary>
        public DefaultScope Result { get; private set; } = DefaultScope.None;

        public SetDefaultScopeDialog(XamlRoot xamlRoot, string title, string body,
                                     string appsText, string gamesText, string cancelText)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            Title = title;
            BodyText.Text = body;

            PrimaryButtonText = appsText;
            SecondaryButtonText = gamesText;
            CloseButtonText = cancelText;

            PrimaryButtonClick += (s, e) => Result = DefaultScope.Apps;
            SecondaryButtonClick += (s, e) => Result = DefaultScope.Games;

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
