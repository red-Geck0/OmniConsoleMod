using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 設定介面 UserControl。
    /// 負責平台卡片管理、NavigationView 頁面切換、自訂平台對話方塊及設定手把輪詢。
    /// </summary>
    public sealed partial class SettingsPage : UserControl
    {
        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>手把 B 鍵（導覽未展開時）或「退出」按鈕點選時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        /// <summary>手把 Menu 鍵觸發，通知 MainWindow 直接啟動目前選取的平台（跳過手動 FSE 切換流程）。</summary>
        public event EventHandler? LaunchPlatformDirectlyRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 ShowWindow (退出隱藏) 使用。</summary>
        public IntPtr Hwnd { get; set; }

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private readonly ResourceLoader _resourceLoader = new();
        private GamepadNavigationService? _gamepadNavigationService;

        // 設定介面的平台卡片清單與目前選取的平台 Id
        private List<PlatformCardItem> _cardItems = [];
        private string _selectedPlatformId = "";

        // 目前顯示的設定導覽頁面（General / Advanced / Troubleshoot）
        private string _currentNavTag = "General";

        // 匯出成功提示的自動關閉計時器（2 秒後關閉 TeachingTip）
        private readonly DispatcherTimer _exportTipTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // 關於頁「已複製」InfoBar 的自動關閉計時器（2 秒後關閉）
        private readonly DispatcherTimer _aboutCopyConfirmTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // ContentDialog 重入防護：平板互動模式下 Dialog 關閉動畫較慢，
        // 手把快速按 A 可能在前一個 Dialog 尚未完全移除時觸發第二次 ShowAsync() 導致崩潰
        private bool _isDialogOpen;

        // 防止檢查更新重複觸發
        private bool _isCheckingUpdate;

        // 防止關於頁重新整理重複觸發
        private bool _isRefreshingAbout;

        // 下載更新的取消 token
        private CancellationTokenSource? _downloadCts;

        // ── Win32 API ───────────────────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterApplicationRestart(string commandLine, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        // 手把映射編輯器待辦：從 Protocol 進來時暫存 profileId，ShowSettings 取出
        private string? _pendingEditProfileId;

        public SettingsPage()
        {
            InitializeComponent();
            _exportTipTimer.Tick += (_, _) =>
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;
            };
            _aboutCopyConfirmTimer.Tick += (_, _) =>
            {
                _aboutCopyConfirmTimer.Stop();
                AboutCopyConfirmTeachingTip.IsOpen = false;
            };
            WireGamepadMappingControls();
        }

        /// <summary>掛 GamepadProfileListView / GamepadProfileEditor 的事件路由（編輯／關閉／刪除／子對話方塊通知）。</summary>
        private void WireGamepadMappingControls()
        {
            GamepadProfileList.EditRequested += (s, profileId) => OpenEditorFor(profileId);
            GamepadProfileList.NewProfileRequested += (s, e) => OpenEditorFor(null);
            GamepadProfileEditor.Closed += (s, e) => CloseEditor();
            GamepadProfileEditor.Deleted += (s, e) => CloseEditor();

            EventHandler<bool> onDialogActive = (s, active) =>
            {
                if (active) StopGamepadPolling();
                else StartGamepadPolling();
            };
            GamepadProfileEditor.DialogActiveChanged += onDialogActive;
            GamepadProfileList.DialogActiveChanged += onDialogActive;
        }

        /// <summary>從 LocalSettings 取 Protocol 進來時暫存的 profileId；取出後立刻刪除。</summary>
        private void ConsumePendingEditProfileRequest()
        {
            _pendingEditProfileId = PendingEditProfileService.TryConsume();
        }

        /// <summary>進入手把映射分頁的初始化：更新清單 → 若有 Protocol 帶入則直接開編輯器、否則顯清單。</summary>
        private void InitGamepadMappingPage()
        {
            try { GamepadProfileList.Refresh(); } catch { }

            if (!string.IsNullOrEmpty(_pendingEditProfileId))
            {
                var id = _pendingEditProfileId;
                _pendingEditProfileId = null;
                OpenEditorFor(id);
                return;
            }

            VisualStateManager.GoToState(this, "GamepadMappingListVisible", false);
            UpdateGamepadHints();
            GamepadProfileList.FocusList();
        }

        /// <summary>切到編輯器：載入目標 profile（profileId 為 null 則新建）→ 切 VSM → 更新提示按鈕 → 把焦點移到編輯器首個控制項。</summary>
        private void OpenEditorFor(string? profileId)
        {
            DebugLogger.Log($"[SettingsPage] OpenEditorFor(profileId={profileId ?? "null"}) called");
            try
            {
                GamepadProfileEditor.Load(profileId);
                VisualStateManager.GoToState(this, "GamepadMappingEditorVisible", false);
                UpdateGamepadHints();
                // 直接同步呼叫，不透過 DispatcherQueue.TryEnqueue(Low, ...) 排隊：
                // GamepadNavigationService 的手把輪詢計時器（33ms 間隔、Normal 優先序）在此時已經
                // 在跑（見 SettingsPage.ShowSettings 的 StartGamepadPolling），會持續佔用佇列，
                // 導致 Low 優先序的項目永遠排不到、FocusFirstControl 根本沒被執行到，
                // 造成編輯器完全沒有起始焦點、D-pad 一直播放「無法移動」音效。
                // FocusFirstControl 內部本身已有 Loaded 事件 + CompositionTarget.Rendering 重試機制
                // 處理版面尚未就緒的情況，不需要靠 DispatcherQueue 優先序來延後執行。
                GamepadProfileEditor.FocusFirstControl();
            }
            catch
            {
                VisualStateManager.GoToState(this, "GamepadMappingListVisible", false);
                UpdateGamepadHints();
            }
        }

        /// <summary>退出編輯器回清單頁；更新清單並把焦點還給它。</summary>
        private void CloseEditor()
        {
            VisualStateManager.GoToState(this, "GamepadMappingListVisible", false);
            UpdateGamepadHints();
            try { GamepadProfileList.Refresh(); } catch { }
            GamepadProfileList.FocusList();
        }

        /// <summary>目前是否在手把映射編輯器頁。</summary>
        private bool IsGamepadMappingEditorVisible =>
            _currentNavTag == "GamepadMapping" && GamepadProfileEditor.Visibility == Visibility.Visible;

        /// <summary>目前是否在手把映射清單頁。</summary>
        private bool IsGamepadMappingListVisible =>
            _currentNavTag == "GamepadMapping" && GamepadProfileEditor.Visibility != Visibility.Visible;

        /// <summary>處理 B 鍵：編輯器頁 = 儲存並返回，清單頁交給一般退出邏輯。</summary>
        private bool TryHandleGamepadMappingBackKey()
        {
            if (IsGamepadMappingEditorVisible)
            {
                GamepadProfileEditor.Save();
                return true;
            }
            return false;
        }

        /// <summary>處理 X 鍵：編輯器頁=刪目前 profile；清單頁=刪選中項。</summary>
        private bool TryHandleGamepadMappingDeleteKey()
        {
            if (IsGamepadMappingEditorVisible)
            {
                if (GamepadProfileEditor.CanDelete) GamepadProfileEditor.DeleteCurrent();
                return true;
            }
            if (IsGamepadMappingListVisible)
            {
                _ = GamepadProfileList.DeleteSelectedAsync();
                return true;
            }
            return false;
        }

        /// <summary>X 鍵提示按鈕的滑鼠點選處理（清單頁 / 編輯器頁都共用）。</summary>
        private void DeleteProfileHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsGamepadMappingEditorVisible) GamepadProfileEditor.DeleteCurrent();
            else if (IsGamepadMappingListVisible) _ = GamepadProfileList.DeleteSelectedAsync();
        }

        /// <summary>B 鍵儲存並返回的提示按鈕滑鼠點選處理（編輯器頁專用）。</summary>
        private void SaveProfileHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsGamepadMappingEditorVisible) GamepadProfileEditor.Save();
        }

        /// <summary>Y 鍵將焦點 profile 設為預設的提示按鈕滑鼠點選處理（清單頁專用）。</summary>
        private void SetDefaultProfileHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsGamepadMappingListVisible) GamepadProfileList.SetSelectedAsDefault();
        }

        /// <summary>判斷節點是否為祖先元素的子孫（含自身）。用於辨識焦點是否落在清單範圍內。</summary>
        private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ── 設定介面初始化 ────────────────────────────────────────────────────

        /// <summary>
        /// 初始化設定介面各控制項狀態，並啟動手把輪詢與平台可用性查詢。
        /// 可見性切換由 <see cref="OmniConsole.MainWindow.ShowSettings"/> 負責，本方法於其後呼叫。
        /// </summary>
        public void ShowSettings()
        {
            DebugLogger.Log($"[DIAG] SettingsPage.ShowSettings pid={Environment.ProcessId} tick={Environment.TickCount64}");
            // Protocol 帶入的待編輯 appId / displayName 在這裡先取出，下方視情況用來自動跳到手把映射編輯器
            ConsumePendingEditProfileRequest();

            // PhantomLink 可能已直接改動 Shared.ini，先從共用儲存同步回 LocalSettings
            SettingsService.ReloadFromSharedStore();

            // 先設好狀態，再賦值 SelectedItem（賦值會觸發 SelectionChanged → UpdateGamepadHints）
            _currentNavTag = "General";
            VisualStateManager.GoToState(this, "General", false);

            // 還原上次儲存的選取狀態；LoadPlatformCards 會依此還原 GridView 的選取項並捲入可視範圍
            // （系統平台與使用者自訂平台合併於單一卡片網格，不再需要依平台歸屬切換索引標籤）。
            _selectedPlatformId = SettingsService.GetDefaultPlatform().Id;

            // 初始化 NavigationView，預設選取第一個「一般」項目
            // 賦值觸發 SettingsNav_SelectionChanged → UpdateGamepadHints()，此時狀態已正確
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            LoadPlatformCards();

            // 顯示版本號
            VersionText.Text = $"v{SettingsService.GetAppVersion()}";

            // FSE 不可用時反灰按鈕而非隱藏
            ResetGameBarButton.IsEnabled = FseService.CanActivate();

            UpdateSettingsDescription();

            // PhantomKey 已改為 FSE 常駐，不再有獨立開關。
            // UI 開關雖然註解保留，但這裡直接判斷 FSE → 啟動，確保更新重啟後恢復。
            //UsePhantomKeySwitch.IsOn = SettingsService.GetUsePhantomKey();
            //if (FseService.IsActive() && UsePhantomKeySwitch.IsOn)
            //    PhantomKeyService.Start();
            if (FseService.IsActive())
                PhantomKeyService.Start();

            // 還原 Steam In-Game Overlay 開關狀態（PhantomKey 恆為啟用，此開關恆可用）
            UsePhantomKeySteamInGameOverlaySwitch.IsOn = SettingsService.GetUsePhantomKeySteamInGameOverlay();
            UsePhantomKeySteamInGameOverlaySwitch.IsEnabled = true;

            // [MOVED] Gamepad Mouse Mode 開關已移至 OmniNav 頁（GamepadProfileListView.SyncMouseMode）；
            // Advanced 頁不再持有該控制項，相關還原邏輯停用。

            // 還原導覽音效開關狀態
            NavigationSoundsSwitch.IsOn = SettingsService.GetEnableNavigationSounds();
            DebugLoggingSwitch.IsOn = SettingsService.GetEnableDebugLogging();
            BootVideoSwitch.IsOn = SettingsService.GetEnableBootVideo();
            BootVideoSyncSwitch.IsOn = SettingsService.GetBootVideoPlayBeforeLaunch();
            UpdateBootVideoFileText();

            // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），強制走 SettingsService 預設值。
            //
            // // 還原 Game Bar 媒體櫃的開關狀態
            // UseGameBarLibrarySwitch.IsOn = SettingsService.GetUseGameBarLibraryForSettings();
            //
            // // 還原 Passthrough 開關狀態
            // EnablePassthroughSwitch.IsOn = SettingsService.GetEnablePassthrough();

            // 還原自動檢查更新開關狀態，並顯示進階區版本號
            AutoUpdateCheckSwitch.IsOn = SettingsService.GetAutoUpdateCheckEnabled();
            AdvancedVersionText.Text = SettingsService.GetAppVersion();

            // 讀取快取的更新資訊
            ShowSettingsUpdateInfoBar();
            ShowCachedUpdateStatus();
            CheckDeveloperMode(); // 未啟用開發人員模式時顯示警告並停用下載按鈕

            // 自動檢查更新（跨日 + 開關啟用時）
            if (UpdateCheckService.ShouldAutoCheck())
                _ = AutoCheckForUpdatesAsync();

            StartGamepadPolling();

            // 由 Protocol 帶入待編輯 profileId 時，把 NavigationView 切到「手把映射」分頁
            //（SelectionChanged → InitGamepadMappingPage 會處理 _pendingEditProfileId 開編輯器）
            if (!string.IsNullOrEmpty(_pendingEditProfileId))
            {
                foreach (var item in SettingsNav.MenuItems)
                {
                    if (item is NavigationViewItem nav && nav.Tag?.ToString() == "GamepadMapping")
                    {
                        SettingsNav.SelectedItem = nav;
                        break;
                    }
                }
            }
        }

        // ── VSM 狀態輔助方法 ─────────────────────────────────────────────────────

        /// <summary>
        /// 依目前導覽頁面更新底部手把提示列的按鍵圖示。應於 <see cref="_currentNavTag"/> 變更後呼叫。
        /// </summary>
        private void UpdateGamepadHints()
        {
            if (_currentNavTag == "GamepadMapping")
            {
                // 先把 General 頁的 Y/X 等 setter 清回基礎狀態，再套用編輯器/清單頁專屬手把提示列
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                bool editor = IsGamepadMappingEditorVisible;
                VisualStateManager.GoToState(this, editor ? "GamepadMappingEditorTab" : "GamepadMappingListTab", false);
                GamepadHintY.Visibility = Visibility.Collapsed;
                GamepadHintMenu.Visibility = Visibility.Collapsed;
                GamepadHintXDelete.Visibility = (editor ? GamepadProfileEditor.CanDelete : GamepadProfileList.HasItems)
                    ? Visibility.Visible : Visibility.Collapsed;
                GamepadHintYNewProfile.Visibility = editor ? Visibility.Collapsed : Visibility.Visible;
                return;
            }
            if (_currentNavTag != "General")
            {
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                GamepadHintY.Visibility = Visibility.Collapsed;
                GamepadHintMenu.Visibility = Visibility.Collapsed;
                // 還原映射頁可能留下的特殊提示按鈕（離開時要藏回去；Exit 要顯示）
                GamepadHintXDelete.Visibility = Visibility.Collapsed;
                GamepadHintYNewProfile.Visibility = Visibility.Collapsed;
                GamepadHintBSaveReturn.Visibility = Visibility.Collapsed;
                GamepadHintExit.Visibility = Visibility.Visible;
                return;
            }
            // 從手把映射回到 General 時也還原特殊提示按鈕
            GamepadHintXDelete.Visibility = Visibility.Collapsed;
            GamepadHintYNewProfile.Visibility = Visibility.Collapsed;
            GamepadHintBSaveReturn.Visibility = Visibility.Collapsed;
            GamepadHintExit.Visibility = Visibility.Visible;

            // 系統/使用者平台合併於單一卡片網格，不再有索引標籤：
            // Y（新增）一律可用（首次新增會先跳出免責聲明對話方塊）；
            // X（編輯）依目前焦點卡片是否為自訂平台動態決定，見 UpdateEditHintVisibility；
            // Menu（直接啟動）僅需 FSE 啟用中。
            GamepadHintY.Visibility = Visibility.Visible;
            UpdateEditHintVisibility();
            GamepadHintMenu.Visibility = FseService.IsActive() ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 依目前焦點是否落在自訂平台卡片上，更新 X（編輯）手把提示的可見度。
        /// 於 <see cref="UpdateGamepadHints"/> 及 PlatformGridView 的 GotFocus 事件呼叫。
        /// </summary>
        private void UpdateEditHintVisibility()
        {
            if (_currentNavTag != "General") return;
            // UpdateGamepadHints 可能在 ShowSettings 初始化早期、頁面尚未加入視覺樹（XamlRoot 尚未建立）
            // 時就被觸發（例如指定 SettingsNav.SelectedItem 觸發 SelectionChanged）；此時呼叫
            // FocusManager.GetFocusedElement(null) 會拋出 ArgumentException（WinRT: xamlRoot），
            // 故需先確認 XamlRoot 已就緒。
            if (this.XamlRoot == null)
            {
                GamepadHintX.Visibility = Visibility.Collapsed;
                return;
            }
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            bool isCustomFocused = focused is GridViewItem { Content: PlatformCardItem { IsCustom: true } };
            GamepadHintX.Visibility = isCustomFocused ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>PlatformGridView 焦點變更（含子項 GridViewItem 焦點事件的冒泡）：即時更新 X 編輯提示。</summary>
        private void PlatformGridView_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateEditHintVisibility();
        }


        // ── NavigationView 事件 ───────────────────────────────────────────────

        /// <summary>
        /// 處理 NavigationView 選項變更，切換內容頁面。
        /// </summary>
        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            DebugLogger.Log($"[DIAG] SettingsNav_SelectionChanged tick={Environment.TickCount64} tag={(args.SelectedItemContainer as NavigationViewItem)?.Tag}");
            if (args.SelectedItemContainer is NavigationViewItem selectedItem)
            {
                if (selectedItem.Tag?.ToString() is not string tag) return;

                // 切換頁面並更新提示列；NavigationViewItem 預設無 Sound 觸發，補 Invoke 音讓滑鼠路徑也有回饋。
                // 走 GamepadNavigationService.PlaySound 共用 50ms 去重表，避免手把 A 鍵主路徑與本事件雙觸發。
                _currentNavTag = tag;
                VisualStateManager.GoToState(this, tag, false);
                UpdateGamepadHints();
                GamepadNavigationService.PlaySound(Microsoft.UI.Xaml.ElementSoundKind.Invoke);

                // 切到關於頁時，每次都重新擷取一次環境快照（PhantomKey 狀態在工作階段中變動）
                if (tag == "About")
                {
                    LoadAboutPageContent();
                }
                else if (tag == "GamepadMapping")
                {
                    InitGamepadMappingPage();
                }

                // 切換頁面後把焦點移到該頁首個控制項，避免焦點滯留在側邊選單。
                // 不用 DispatcherQueue.TryEnqueue(Low, ...) 排隊：GamepadNavigationService 的手把輪詢
                // 計時器（Normal 優先序、33ms 間隔）此時通常已在跑，會持續佔用佇列，讓 Low 優先序
                // 項目遲遲排不到、焦點設定永遠沒機會執行。改掛 CompositionTarget.Rendering 逐影格重試。
                StartFocusRetryLoop(tag);
            }
        }

        /// <summary>
        /// 掛 CompositionTarget.Rendering，重複呼叫 FocusFirstElementForPage 數個影格（Focus() 呼叫本身
        /// 是幂等的，重複呼叫無副作用），確保容器尚未 realize 的情況下最終仍能拿到焦點；逾時 1 秒後解除掛勾。
        /// </summary>
        private void StartFocusRetryLoop(string tag)
        {
            var deadline = DateTime.UtcNow.AddSeconds(1);
            EventHandler<object>? onRendering = null;
            onRendering = (s, e) =>
            {
                FocusFirstElementForPage(tag);
                if (DateTime.UtcNow >= deadline)
                    CompositionTarget.Rendering -= onRendering;
            };
            CompositionTarget.Rendering += onRendering;
        }

        /// <summary>切換設定頁後，把控制器焦點移到該頁的首個控制項。</summary>
        private void FocusFirstElementForPage(string tag)
        {
            switch (tag)
            {
                case "General":
                    (PlatformGridView.ContainerFromIndex(0) as UIElement)?.Focus(FocusState.Programmatic);
                    break;
                case "Advanced":
                    if (DownloadInstallButton.Visibility == Visibility.Visible && DownloadInstallButton.IsEnabled)
                        DownloadInstallButton.Focus(FocusState.Programmatic);
                    else
                        CheckForUpdatesButton.Focus(FocusState.Programmatic);
                    break;
                case "GamepadMapping":
                    // Focus the profile list (so OmniNav is selected first), not the New Profile button below
                    if (GamepadProfileList != null && IsGamepadMappingListVisible)
                        GamepadProfileList.FocusList();
                    else
                        (FocusManager.FindFirstFocusableElement(GamepadMappingPage) as UIElement)?.Focus(FocusState.Programmatic);
                    break;
                case "Troubleshoot":
                    ResetGameBarButton.Focus(FocusState.Programmatic);
                    break;
                case "About":
                    CopyAboutButton.Focus(FocusState.Programmatic);
                    break;
                default:
                    (FocusManager.FindFirstFocusableElement(this) as UIElement)?.Focus(FocusState.Programmatic);
                    break;
            }
        }

        // ── 關於頁 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 擷取環境快照並更新關於頁各文字區塊。
        /// 在背景執行緒取資料、再回 UI 執行緒設值。
        /// </summary>
        private async void LoadAboutPageContent()
        {
            if (_isRefreshingAbout) return;
            _isRefreshingAbout = true;

            // 進度環用 Opacity 而非 Visibility 切換：保持佔位（20×20），避免顯隱時推動按鈕 row 寬度。
            RefreshAboutProgressRing.Opacity = 1;
            RefreshAboutProgressRing.IsActive = true;

            var delayTask = Task.Delay(500);
            var snapshot = await Task.Run(() => AboutInfoService.GetEnvironmentSnapshot());
            await delayTask;

            ApplyAboutSnapshot(snapshot);
            RefreshAboutProgressRing.IsActive = false;
            RefreshAboutProgressRing.Opacity = 0;
            _isRefreshingAbout = false;
        }

        /// <summary>
        /// 將 <see cref="AboutInfoService.EnvironmentSnapshot"/> 套用到「關於」分頁的各 UI 欄位。
        /// </summary>
        private void ApplyAboutSnapshot(AboutInfoService.EnvironmentSnapshot s)
        {
            AboutOmniConsoleVersion.Text = LocalizeForUI(s.Versions.OmniConsole);
            AboutPhantomBridgeVersion.Text = LocalizeForUI(s.Versions.PhantomBridge);
            // PhantomKey 同時顯示套件內版本與已部署副本版本，便於診斷複製失敗或舊副本殘留。
            AboutPhantomKeyVersion.Text = s.Versions.PhantomKey == s.Versions.PhantomKeyDeployed
                ? LocalizeForUI(s.Versions.PhantomKey)
                : $"{LocalizeForUI(s.Versions.PhantomKey)} → {LocalizeForUI(s.Versions.PhantomKeyDeployed)}";
            AboutPhantomLinkVersion.Text = LocalizeForUI(s.Versions.PhantomLink);

            // PhantomKey 健康狀況
            ApplyPhantomKeyHealth(s.PhantomKey);

            AboutXfsetToolStatus.Text = FormatXfsetToolForUI(s.Xfset);
            AboutXfsetPhysPanelStatus.Text = FormatPhysPanelForUI(s.Xfset);

            AboutSystemText.Text = $"{LocalizeForUI(s.Hardware.SystemManufacturer)} / {LocalizeForUI(s.Hardware.SystemProductName)}";
            AboutBaseboardText.Text = $"{LocalizeForUI(s.Hardware.BaseboardManufacturer)} / {LocalizeForUI(s.Hardware.BaseboardProduct)}";
            AboutCpuText.Text = FormatCpuForUI(s.Hardware);
            AboutRamText.Text = FormatBytesForUI(s.Hardware.RamTotalBytes);
            AboutGpuText.Text = FormatGpuForUI(s.Hardware);

            AboutWindowsBuildText.Text = LocalizeForUI(s.WindowsBuild);
            AboutFseStateText.Text = s.FseState;

            AboutMaxTouchPointsText.Text = s.MaxTouchPoints == 0
                ? $"{s.MaxTouchPoints} ({_resourceLoader.GetString("MaxTouchPoints_NoTouch")})"
                : s.MaxTouchPoints.ToString(CultureInfo.InvariantCulture);
            AboutLocaleText.Text = LocalizeForUI(s.Locale);
            AboutCountryRegionText.Text = LocalizeForUI(s.CountryRegion);
            AboutDeviceRegionText.Text = LocalizeForUI(s.DeviceRegion);
            AboutCapturedAtText.Text = s.CapturedAt.ToString(
                "yyyy-MM-dd HH:mm:ss zzz",
                CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把資料層的固定英文回退字串（"(unknown)" / "(not installed)"）替換為在地化字串供 UI 顯示。
        /// 資料層保持英文常數有助於 Markdown 輸出的可讀性（貼到 GitHub Issue 不會帶非 ASCII 字串）。
        /// </summary>
        private string LocalizeForUI(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw == "(unknown)") return _resourceLoader.GetString("Common_Unknown");
            if (raw == "(not installed)") return _resourceLoader.GetString("Common_NotInstalled");
            return raw;
        }

        /// <summary>
        /// 把 XFSET 主程式安裝狀態格式化為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatXfsetToolForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.ToolInstalled) return _resourceLoader.GetString("XfsetStatus_NotInstalled");
            return $"{_resourceLoader.GetString("XfsetStatus_Installed")} ({x.ToolVersion})";
        }

        /// <summary>
        /// 把 PhysPanelCS 安裝狀態與 touchservice 執行狀態組合為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatPhysPanelForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.PhysPanelInstalled) return _resourceLoader.GetString("XfsetStatus_NotInstalled");

            string touchKey = x.TouchService switch
            {
                AboutInfoService.TouchServiceState.Running => "XfsetStatus_TouchServiceRunning",
                AboutInfoService.TouchServiceState.NotConfigured => "XfsetStatus_TouchServiceNotRunning",
                _ => "XfsetStatus_TouchServiceUnknown",
            };

            return $"{_resourceLoader.GetString("XfsetStatus_Installed")} ({x.PhysPanelVersion}), {_resourceLoader.GetString(touchKey)}";
        }

        /// <summary>
        /// 把位元組數格式化為設定頁顯示用的可讀字串（≥1 GiB 用 GB，否則用 MB）。
        /// </summary>
        private string FormatBytesForUI(ulong bytes)
        {
            if (bytes == 0) return _resourceLoader.GetString("Common_Unknown");
            const double GiB = 1024.0 * 1024.0 * 1024.0;
            double gib = bytes / GiB;
            return gib >= 1.0
                ? gib.ToString("0.# GB", CultureInfo.InvariantCulture)
                : (bytes / (1024.0 * 1024.0)).ToString("0 MB", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把 CPU 頻率（MHz）格式化為設定頁顯示用字串：≥1000 顯示 GHz，否則顯示 MHz。
        /// </summary>
        private string FormatMhzForUI(int mhz)
        {
            if (mhz <= 0) return _resourceLoader.GetString("Common_Unknown");
            return mhz >= 1000
                ? (mhz / 1000.0).ToString("0.00 GHz", CultureInfo.InvariantCulture)
                : $"{mhz} MHz";
        }

        /// <summary>
        /// 把 CPU 名稱、頻率、實體/邏輯核心數組合為設定頁顯示用的單行字串。
        /// </summary>
        private string FormatCpuForUI(AboutInfoService.HardwareInfo h)
        {
            // 顯示為「<名稱> (<時脈>, <實體>C/<邏輯>T)」
            return $"{LocalizeForUI(h.CpuName)} ({FormatMhzForUI(h.CpuMhz)}, {h.CpuPhysicalCores}C/{h.CpuLogicalCores}T)";
        }

        /// <summary>
        /// 把 GPU 清單（名稱、VRAM、驅動程式版本與日期）格式化為設定頁顯示用的多行字串。
        /// </summary>
        private string FormatGpuForUI(AboutInfoService.HardwareInfo h)
        {
            // 多張顯示卡，每張一行；驅動版本/日期各自顯示
            if (h.Gpus.Count == 0) return _resourceLoader.GetString("Common_Unknown");
            return string.Join(Environment.NewLine,
                h.Gpus.Select(g => $"{LocalizeForUI(g.Name)} ({FormatBytesForUI(g.VramBytes)} VRAM, {LocalizeForUI(g.DriverVersion)} / {LocalizeForUI(g.DriverDate)})"));
        }

        /// <summary>
        /// 把 PhantomKeyHealth 紀錄投到對應文字區塊。未在跑時將細節欄為 dash。
        /// </summary>
        private void ApplyPhantomKeyHealth(AboutInfoService.PhantomKeyHealth h)
        {
            if (!h.ProcessRunning)
            {
                AboutPhantomKeyProcessText.Text = _resourceLoader.GetString("PhantomKeyHealth_NotRunning");
                AboutPhantomKeyUptimeText.Text = "—";
                AboutPhantomKeyIntegrityText.Text = "—";
                AboutPhantomKeyResponsivenessText.Text = "—";
                return;
            }

            AboutPhantomKeyProcessText.Text = _resourceLoader.GetString("PhantomKeyHealth_Running");

            AboutPhantomKeyUptimeText.Text = FormatUptimeForUI(h.Uptime);
            AboutPhantomKeyIntegrityText.Text = h.IntegrityLevel == AboutInfoService.IntegrityLevel.Unknown
                ? _resourceLoader.GetString("Common_Unknown")
                : h.IntegrityLevel.ToString();
            AboutPhantomKeyResponsivenessText.Text = FormatResponsivenessForUI(h);
        }

        /// <summary>
        /// 把 PhantomKey Uptime 格式化為設定頁顯示用字串，依量級裁切顯示精度；非正值回 dash。
        /// </summary>
        private static string FormatUptimeForUI(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero) return "—";
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        /// <summary>
        /// 把 PhantomKey 健康分級轉成設定頁顯示用的在地化描述（含延遲毫秒）。
        /// </summary>
        private string FormatResponsivenessForUI(AboutInfoService.PhantomKeyHealth h)
        {
            return h.Responsiveness switch
            {
                AboutInfoService.PhantomKeyResponsiveness.Responsive
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Responsive"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Busy
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Busy"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Stuck
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Stuck"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Hung
                    => _resourceLoader.GetString("PhantomKeyResp_Hung"),
                AboutInfoService.PhantomKeyResponsiveness.NoPingWindow
                    => _resourceLoader.GetString("PhantomKeyResp_NoPingWindow"),
                AboutInfoService.PhantomKeyResponsiveness.NotRunning
                    => _resourceLoader.GetString("PhantomKeyHealth_NotRunning"),
                _ => "—",
            };
        }

        /// <summary>
        /// 複製關於頁的環境快照到剪貼簿，供使用者貼到 GitHub Issue 協助回報問題。
        /// </summary>
        private void CopyAboutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var snapshot = AboutInfoService.GetEnvironmentSnapshot();
                var markdown = AboutInfoService.FormatAsMarkdown(snapshot);

                var dataPackage = new DataPackage();
                dataPackage.SetText(markdown);
                Clipboard.SetContent(dataPackage);

                // 同步把畫面上的快照重新整理為這次複製的版本，使顯示與剪貼簿一致
                ApplyAboutSnapshot(snapshot);

                AboutCopyConfirmTeachingTip.IsOpen = true;
                _aboutCopyConfirmTimer.Stop();
                _aboutCopyConfirmTimer.Start();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[SettingsPage] CopyAboutButton_Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新整理關於頁所有欄位。
        /// 走 LoadAboutPageContent 路徑。
        /// </summary>
        private void RefreshAboutButton_Click(object sender, RoutedEventArgs e)
        {
            LoadAboutPageContent();
        }

        /// <summary>
        /// 依關於頁實際可用寬度切換雙欄/單欄版型。閾值 1416 = 兩欄各 700 + ColumnSpacing 16。
        /// </summary>
        private void AboutPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 用 ViewportWidth（ScrollViewer 實際給內容的可用寬度）而非 e.NewSize.Width。
            const double DualColumnThreshold = 1416;
            double newSize = e.NewSize.Width;
            double viewport = AboutPage.ViewportWidth;
            double actualWidth = AboutPage.ActualWidth;
            double available = viewport > 0 ? viewport : (actualWidth > 0 ? actualWidth : newSize);
            string targetState = available >= DualColumnThreshold ? "WideAboutState" : "NarrowAboutState";
            // VSG 掛在 SettingsPage 根 Grid 上，與 SettingsNavPage / GeneralContent / GamepadHints 並列。
            VisualStateManager.GoToState(this, targetState, false);
        }

        // ── 平台可用性 ────────────────────────────────────────────────────────

        /// <summary>
        /// 非同步查詢所有平台的安裝狀態，更新 IsAvailable 後重新指定 ItemsSource 重新整理 OneTime 繫結。
        /// 若目前選取的平台不可用，自動切換至第一個可用的平台。
        /// </summary>
        private async Task LoadPlatformAvailabilityAsync()
        {
            // 「新增自訂平台」動作卡非真實平台，排除於可用性查詢外（其 IsAvailable 恆保持預設值 true）。
            var checkable = _cardItems.Where(c => !c.IsAddNewCard).ToList();
            bool[] available = await Task.WhenAll(
                checkable.Select(c => ProcessLauncherService.CheckPlatformAvailableAsync(c.Platform)));

            for (int i = 0; i < checkable.Count; i++)
            {
                checkable[i].IsAvailable = available[i];
            }

            // 若目前選取的平台已停用，先調整選取的 Id
            var currentSelected = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (currentSelected is { IsAvailable: false })
            {
                var firstAvailable = _cardItems.FirstOrDefault(c => c.IsAvailable);
                if (firstAvailable != null)
                {
                    _selectedPlatformId = firstAvailable.Id;
                }
                else
                {
                    // 所有平台都不可用，清除選取 Id
                    _selectedPlatformId = "";
                }
            }

            // 重新指定 ItemsSource 讓 OneTime 繫結重新求值（CardOpacity 依最新 IsAvailable 更新）
            PlatformGridView.ItemsSource = null;
            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }
        }

        // ── 平台卡片事件 ──────────────────────────────────────────────────────

        /// <summary>
        /// 處理 GridView 選取狀態變更。
        /// 「新增自訂平台」動作卡：還原為前次選取並改觸發新增流程，不視為一般選取。
        /// 不可用的平台：自訂平台允許選取（以便透過 X 編輯修正路徑）；系統平台若有其他可用選項則還原。
        /// </summary>
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is not PlatformCardItem selected) return;

            if (selected.IsAddNewCard)
            {
                var previousForAdd = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
                PlatformGridView.SelectedItem = previousForAdd;
                _ = TryAddCustomPlatformAsync();
                return;
            }

            if (!selected.IsAvailable)
            {
                if (selected.IsCustom)
                {
                    // 自訂平台：允許選取不可用的平台（以便透過 X 編輯修正路徑），但不儲存為預設
                    return;
                }

                // 系統平台：若有其他可用平台，還原為上一個有效選取
                if (_cardItems.Any(c => c.IsAvailable && !c.IsAddNewCard))
                {
                    var previous = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
                    PlatformGridView.SelectedItem = previous;
                    return;
                }
                // 完全沒有可用平台：允許選取（啟動時會顯示錯誤訊息）
            }

            _selectedPlatformId = selected.Id;

            // 選取即儲存：先查系統平台，再查使用者平台
            var platform = PlatformCatalog.FindById(_selectedPlatformId)
                ?? UserPlatformStore.FindById(_selectedPlatformId)
                ?? PlatformCatalog.All[0];
            SettingsService.SetDefaultPlatform(platform);
            SettingsService.SaveCurrentVersion();
            UpdateSettingsDescription();
        }

        /// <summary>
        /// 更新標題下方的描述文字，顯示目前預設平台名稱。
        /// </summary>
        private void UpdateSettingsDescription()
        {
            var platform = SettingsService.GetDefaultPlatform();
            var name = ProcessLauncherService.GetPlatformDisplayName(platform);
            // 拆成前綴 + 平台名稱兩個 Run，平台名稱套粗體。三語系版本 "{0}" 皆位於字串尾端。
            string template = _resourceLoader.GetString("SettingsDescription");
            int idx = template.IndexOf("{0}", StringComparison.Ordinal);
            SettingsDescriptionPrefix.Text = idx >= 0 ? template.Substring(0, idx) : template;
            SettingsDescriptionPlatformName.Text = name;
        }

        /// <summary>
        /// GridView 大小變更時，依可用寬度計算每張卡片的尺寸，使卡片固定填滿 4 欄。
        /// 容器本身有 MaxWidth（見 SettingsPage.xaml）限制卡片不會在寬螢幕上過度放大；
        /// 寬度不足時卡片等比縮小，欄數恆為 4（不再依寬度切換 2/3/4 欄）。
        /// </summary>
        private void PlatformGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PlatformGridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                double availableWidth = e.NewSize.Width;
                const int columns = 4;
                double itemWidth = Math.Floor(availableWidth / columns);
                double remainder = availableWidth - itemWidth * columns;
                // 非整除且餘數極小時 ItemsWrapGrid 因精度問題換行，減 1 迴避
                if (remainder > 0 && remainder < 1)
                    itemWidth -= 1;
                wrapGrid.ItemWidth = itemWidth;
                wrapGrid.ItemHeight = Math.Floor(itemWidth * 0.7); // 維持約 7:10 的高寬比
            }
        }

        // ── 設定控制項事件 ────────────────────────────────────────────────────

        /// <summary>
        /// 重設 Game Bar 並觸發 FSE。先透過 <see cref="FseService.EnsureGameBarReadyAsync"/>
        /// 確保 Game Bar 完全就緒，再以「殺死後重發」機制繞過可能卡住的 FSE 進入對話方塊。
        /// </summary>
        private async void ResetGameBarButton_Click(object sender, RoutedEventArgs e)
        {
            ResetGameBarButton.IsEnabled = false;
            ResetGameBarProgressRing.IsActive = true;
            ResetGameBarProgressRing.Visibility = Visibility.Visible;

            // 1. 強制重啟 Game Bar 並輪詢等待 GameBarFTServer 就緒
            //    （內部會先終止 GameBar.exe 再透過 ms-gamebar:// 重啟）
            await FseService.EnsureGameBarReadyAsync();
            await Task.Delay(500);

            // 2. 再次殺掉以繞過 FSE 進入對話方塊（「殺死後重發」機制），稍待讓系統狀態穩定
            FseService.KillGameBar();
            await Task.Delay(500);

            // [Windows Bug] 從桌面進入 FSE 時，部分應用程式會被最大化並搶走前景焦點
            if (!FseService.IsActive())
                FseService.KillIgnoredBackgroundServices();

            if (FseService.TryActivate())
            {
                // 此應用程式會被重新啟動在 FSE 環境
                ShowWindow(Hwnd, SW_HIDE);
                App.ExitApp();
            }

            ResetGameBarProgressRing.IsActive = false;
            ResetGameBarProgressRing.Visibility = Visibility.Collapsed;
            ResetGameBarButton.IsEnabled = true;
        }

        /// <summary>
        /// PhantomKey 手把輸入開關切換時立即儲存。
        /// 開啟時若在 FSE 模式下立即啟動服務，關閉時終止服務。
        /// 同時連動 Steam In-Game Overlay 開關的啟用狀態。
        /// </summary>
        // PhantomKey 已改為 FSE 常駐；XAML 開關已註解，此 handler 保留但不再被觸發。
        private void UsePhantomKeySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            //SettingsService.SetUsePhantomKey(UsePhantomKeySwitch.IsOn);
            //UsePhantomKeySteamInGameOverlaySwitch.IsEnabled = UsePhantomKeySwitch.IsOn;
            //ApplyMouseModeEnabledState();
            //if (UsePhantomKeySwitch.IsOn && FseService.IsActive())
            //    PhantomKeyService.Start();
            //else if (!UsePhantomKeySwitch.IsOn)
            //    PhantomKeyService.Kill();
        }

        /// <summary>
        /// Steam In-Game Overlay 開關切換時立即儲存（同步寫入 INI）。
        /// </summary>
        private void UsePhantomKeySteamInGameOverlaySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetUsePhantomKeySteamInGameOverlay(UsePhantomKeySteamInGameOverlaySwitch.IsOn);
        }

        // [MOVED] Mouse Mode 開關移至 OmniNav 頁（GamepadProfileListView）。
        // 以下 handler / 反灰邏輯保留為註解，不刪除。
        //
        // private void MouseModeSwitch_Toggled(object sender, RoutedEventArgs e)
        // {
        //     string mode = MouseModeSwitch.IsOn ? SettingsService.MouseModeAuto : SettingsService.MouseModeOff;
        //     SettingsService.SetMouseMode(mode);
        //     ApplyMouseModeEnabledState();
        // }

        /// <summary>
        /// 導覽音效 ToggleSwitch 切換時立即儲存，並即時切換 ElementSoundPlayer 全域狀態。
        /// </summary>
        private void NavigationSoundsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = NavigationSoundsSwitch.IsOn;
            SettingsService.SetEnableNavigationSounds(enabled);
            Microsoft.UI.Xaml.ElementSoundPlayer.State =
                enabled
                    ? Microsoft.UI.Xaml.ElementSoundPlayerState.On
                    : Microsoft.UI.Xaml.ElementSoundPlayerState.Off;
        }

        /// <summary>
        /// 除錯日誌 ToggleSwitch 切換時立即儲存。關閉時 DebugLogger.Log 完全不做檔案 I/O，
        /// 不影響手把導覽等高頻路徑的反應速度。
        /// </summary>
        private void DebugLoggingSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetEnableDebugLogging(DebugLoggingSwitch.IsOn);
        }

        /// <summary>開啟除錯記錄檔所在資料夾（File Explorer）。找不到資料夾時靜默略過。</summary>
        private async void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var path = DebugLogger.GetLogFolderPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.IO.Directory.CreateDirectory(path);
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                await Windows.System.Launcher.LaunchFolderAsync(folder);
            }
            catch { }
        }

        /// <summary>開機影片 ToggleSwitch 切換時立即儲存。</summary>
        private void BootVideoSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetEnableBootVideo(BootVideoSwitch.IsOn);
        }

        /// <summary>開機影片與平台啟動先後順序 ToggleSwitch 切換時立即儲存。</summary>
        private void BootVideoSyncSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetBootVideoPlayBeforeLaunch(BootVideoSyncSwitch.IsOn);
        }

        /// <summary>
        /// 「選擇影片」按鈕：開自製 FilePickerDialog（不支援時退回系統 FileOpenPicker），
        /// 選定後複製一份到 LocalFolder/BootVideo/（見 BootVideoStore），避免直接讀外部路徑
        /// 可能遇到的封裝應用程式檔案存取限制。
        /// </summary>
        private async void BootVideoChooseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                var options = new FilePickerOptions
                {
                    FileTypeFilters = [".mp4"],
                    FilterDisplayName = _resourceLoader.GetString("FilePickerDialog_FilterVideo"),
                };

                var pickerDialog = new FilePickerDialog(this.XamlRoot, _resourceLoader, options);
                StopGamepadPolling();
                var result = await pickerDialog.ShowAsync();
                StartGamepadPolling();

                string? selectedPath = null;
                if (result == ContentDialogResult.Primary)
                    selectedPath = pickerDialog.SelectedFilePath;
                else if (pickerDialog.RequestLegacyPicker)
                    selectedPath = await ShowLegacyFilePickerAsync(options);

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    var storageFile = await StorageFile.GetFileFromPathAsync(selectedPath);
                    string fileName = await BootVideoStore.ImportVideoAsync(storageFile);
                    SettingsService.SetBootVideoFileName(fileName);
                    UpdateBootVideoFileText();
                }
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        /// <summary>依目前是否已匯入影片，更新提示文字：顯示使用者匯入時的原始檔名。</summary>
        private void UpdateBootVideoFileText()
        {
            string displayName = SettingsService.GetBootVideoDisplayName();
            BootVideoFileText.Text = string.IsNullOrEmpty(displayName)
                ? _resourceLoader.GetString("BootVideoFileSetting_None")
                : displayName;
        }

        // [MOVED] 反灰邏輯隨 Mouse Mode 開關移至 OmniNav 頁（GamepadProfileListView.SyncMouseMode）。
        // private void ApplyMouseModeEnabledState(bool? builtInMappingOverride = null)
        // {
        //     bool builtIn = builtInMappingOverride ?? SettingsService.HasBuiltInGamepadMapping();
        //     MouseModeSwitch.IsEnabled = !builtIn;
        //     MouseModeBuiltInMappingNoteText.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
        // }

        // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），Toggled handler 一併停用。
        //
        // /// <summary>
        // /// Game Bar 媒體櫃開關切換時立即儲存。
        // /// 開啟時 Game Bar 的「媒體櫃」按鈕將開啟 OmniConsole 設定介面；關閉時開啟預設遊戲平台。
        // /// </summary>
        // private void UseGameBarLibrarySwitch_Toggled(object sender, RoutedEventArgs e)
        // {
        //     SettingsService.SetUseGameBarLibraryForSettings(UseGameBarLibrarySwitch.IsOn);
        // }
        //
        // /// <summary>
        // /// Passthrough 開關切換時立即儲存。
        // /// 開啟時 Game Bar 的「首頁」與「媒體櫃」按鈕將直接導向預設平台，跳過 OmniConsole。
        // /// </summary>
        // private void EnablePassthroughSwitch_Toggled(object sender, RoutedEventArgs e)
        // {
        //     SettingsService.SetEnablePassthrough(EnablePassthroughSwitch.IsOn);
        // }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 載入平台卡片清單：系統內建平台與使用者自訂平台合併於單一卡片網格（不再分索引標籤），
        /// 尾端固定附加一張「新增自訂平台」動作卡。
        /// </summary>
        private void LoadPlatformCards()
        {
            var systemCards = PlatformCatalog.All
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                    IsCustom = false,
                });

            var userCards = UserPlatformStore.GetAllDefinitions()
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = UserPlatformStore.FindEntryById(p.Id)?.DisplayName ?? p.Id,
                    IsCustom = true,
                });

            var addNewCard = new PlatformCardItem
            {
                Platform = new PlatformDefinition
                {
                    Id = "__add_new__",
                    DisplayNameKey = "",
                    // IconAsset 需為合法 URI（即使該 Image 因 NormalCardVisibility=Collapsed 不會顯示）：
                    // x:Bind 仍會在容器實體化時將此值套用到 Image.Source，空字串會讓 new Uri("") 拋出
                    // UriFormatException 導致整個 GridView（進而整個 Settings 頁）載入失敗。隨便指向一個
                    // 保證存在的既有資源即可，畫面上看不到。
                    IconAsset = "ms-appx:///Assets/Platforms/steam.png",
                    LaunchStrategies = [],
                    AvailabilityStrategy = new LaunchStrategy { Type = LaunchStrategyType.Executable },
                },
                DisplayName = "",
                IsAddNewCard = true,
            };

            _cardItems = systemCards.Concat(userCards).Append(addNewCard).ToList();

            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態並捲入可視範圍（合併清單可能超出單頁高度）
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
                PlatformGridView.ScrollIntoView(selectedCard);
            }

            // 非同步查詢可用性
            _ = LoadPlatformAvailabilityAsync();
        }

        // ── 自訂平台新增／匯入（含首次免責聲明） ─────────────────────────────────

        /// <summary>
        /// 觸發「新增自訂平台」流程：尚未接受免責聲明時先跳出同意對話方塊，
        /// 接受後才開啟 <see cref="ShowPlatformEditDialogAsync"/>（新增模式）。
        /// 取消／拒絕同意則整個流程中止，不開啟新增對話方塊。
        /// </summary>
        private async Task TryAddCustomPlatformAsync()
        {
            if (!SettingsService.GetCustomPlatformConsentAccepted())
            {
                bool accepted = await ShowCustomPlatformConsentDialogAsync();
                if (!accepted) return;
                SettingsService.SetCustomPlatformConsentAccepted(true);
            }
            await ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 顯示自訂平台功能免責聲明對話方塊，回傳使用者是否點選「我了解並接受」。
        /// </summary>
        private async Task<bool> ShowCustomPlatformConsentDialogAsync()
        {
            if (_isDialogOpen) return false;
            _isDialogOpen = true;
            try
            {
                var dialog = new CustomPlatformConsentDialog(this.XamlRoot, _resourceLoader);
                StopGamepadPolling();
                var result = await dialog.ShowAsync();
                StartGamepadPolling();
                return result == ContentDialogResult.Primary && dialog.Accepted;
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 平台匯出 / 匯入 ───────────────────────────────────────────────────

        /// <summary>
        /// 卡片右鍵選單開啟前呼叫：非自訂平台卡片（系統平台／新增動作卡）直接關閉 flyout，不顯示選單。
        /// </summary>
        private void CardContextMenu_Opening(object sender, object e)
        {
            var flyout = sender as MenuFlyout;
            var card = (flyout?.Target as FrameworkElement)?.DataContext as PlatformCardItem;
            if (card is not { IsCustom: true })
                flyout?.Hide();
        }

        /// <summary>
        /// 卡片右鍵選單「匯出」點選時，將平台設定序列化為 JSON 並複製到剪貼簿。
        /// </summary>
        private void CardExport_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PlatformCardItem card) return;

            var entry = UserPlatformStore.FindEntryById(card.Id);
            if (entry is null) return;

            var dp = new DataPackage();
            dp.SetText(UserPlatformShareService.Export(entry));
            Clipboard.SetContent(dp);

            ExportSuccessTeachingTip.IsOpen = true;
            _exportTipTimer.Stop();
            _exportTipTimer.Start();
        }

        /// <summary>
        /// 「匯入」按鈕點選時，顯示 ImportPlatformDialog（尚未接受自訂平台免責聲明時先跳出同意對話方塊）。
        /// 驗證通過後寫入 UserPlatformStore 並重新載入卡片。
        /// </summary>
        private async void ImportHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;

            if (!SettingsService.GetCustomPlatformConsentAccepted())
            {
                bool accepted = await ShowCustomPlatformConsentDialogAsync();
                if (!accepted) return;
                SettingsService.SetCustomPlatformConsentAccepted(true);
            }

            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                // 若提示仍開著，先強制關閉再顯示 Dialog，
                // 避免 TeachingTip 與 ContentDialog.ShowAsync() 同時存在導致崩潰。
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                var dialog = new ImportPlatformDialog(this.XamlRoot, _resourceLoader);
                StopGamepadPolling();
                var result = await dialog.ShowAsync();
                StartGamepadPolling();
                if (result != ContentDialogResult.Primary || dialog.ResultEntry is null) return;

                UserPlatformStore.Add(dialog.ResultEntry);
                LoadPlatformCards();
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 平台編輯對話方塊 ──────────────────────────────────────────────────

        /// <summary>
        /// 底部提示列「X 編輯」按鈕的滑鼠點選處理。
        /// 編輯目前 GridView 中選取的自訂平台。
        /// </summary>
        private void EditPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is PlatformCardItem { IsCustom: true } card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>開啟系統 FileOpenPicker 作為舊式後備，回傳選取的檔案路徑或 null。</summary>
        private async Task<string?> ShowLegacyFilePickerAsync(FilePickerOptions options)
        {
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = options.ShowImagePreview
                    ? PickerLocationId.PicturesLibrary
                    : PickerLocationId.ComputerFolder;
                foreach (var filter in options.FileTypeFilters)
                    picker.FileTypeFilter.Add(filter);

                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 顯示新增/編輯使用者平台的 PlatformEditDialog。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        private async Task ShowPlatformEditDialogAsync(UserPlatformEntry? existingEntry)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                bool isEdit = existingEntry != null;
                var dialog = new PlatformEditDialog(
                    this.XamlRoot, _resourceLoader, existingEntry);

                ContentDialogResult result;

                // Hide/reopen 迴圈：PlatformEditDialog 的瀏覽按鈕會 Hide() 自己，
                // 由此處協調顯示 FilePickerDialog 後重新開啟 PlatformEditDialog。
                while (true)
                {
                    StopGamepadPolling();
                    result = await dialog.ShowAsync();
                    StartGamepadPolling();

                    if (!dialog.RequestFilePicker) break;

                    // 顯示自製檔案選擇器
                    var pickerDialog = new FilePickerDialog(
                        this.XamlRoot, _resourceLoader, dialog.FilePickerRequest!);
                    StopGamepadPolling();
                    var pickerResult = await pickerDialog.ShowAsync();
                    StartGamepadPolling();

                    string? selectedPath = null;
                    if (pickerResult == ContentDialogResult.Primary)
                    {
                        selectedPath = pickerDialog.SelectedFilePath;
                    }
                    else if (pickerDialog.RequestLegacyPicker)
                    {
                        // 使用者要求系統 FileOpenPicker
                        selectedPath = await ShowLegacyFilePickerAsync(dialog.FilePickerRequest!);
                    }
                    dialog.ApplyFilePickerResult(selectedPath);
                    // 迴圈回去重新開啟 PlatformEditDialog
                }

                if (result == ContentDialogResult.Primary && dialog.ResultEntry != null)
                {
                    var entry = dialog.ResultEntry;

                    // 匯入卡片背景圖（縮放至 800x560）
                    if (dialog.PendingIconPath != null)
                    {
                        if (!string.IsNullOrEmpty(entry.IconFileName))
                            UserPlatformStore.DeleteIconFile(entry.IconFileName);
                        var storageFile = await StorageFile.GetFileFromPathAsync(dialog.PendingIconPath);
                        entry.IconFileName = await UserPlatformStore.ImportIconAsync(storageFile);
                    }

                    if (isEdit)
                        UserPlatformStore.Update(entry);
                    else
                        UserPlatformStore.Add(entry);

                    LoadPlatformCards();
                }
                else if (result == ContentDialogResult.Secondary && isEdit && existingEntry != null)
                {
                    // 刪除平台：從 Store 移除後重新載入合併卡片清單。
                    UserPlatformStore.Delete(existingEntry.Id);

                    var remainingUser = UserPlatformStore.GetAllDefinitions();
                    _selectedPlatformId = remainingUser.Count > 0
                        ? remainingUser[0].Id
                        : PlatformCatalog.All[0].Id;
                    LoadPlatformCards();
                }
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 手把輸入處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 Xbox 手把的輸入輪詢機制。
        /// 若尚未初始化 <see cref="GamepadNavigationService"/>，則會在此建立其實體，
        /// 以 <see cref="SettingsNav"/> 為 XY 焦點根容器，並傳遞各按鍵回呼函式。
        /// </summary>
        public void StartGamepadPolling()
        {
            DebugLogger.Log($"[SettingsPage] StartGamepadPolling() called, serviceAlreadyExists={_gamepadNavigationService != null}");
            if (_gamepadNavigationService == null)
            {
                // LB/RB 未綁定：平台卡片網格已合併系統/自訂平台，不再有索引標籤可切換。
                _gamepadNavigationService = new GamepadNavigationService(
                    this.SettingsNav,
                    this.DispatcherQueue,
                    OnGamepadAButtonPressed,
                    OnGamepadBButtonPressed,
                    onLBPressed: null,
                    onRBPressed: null,
                    OnGamepadXButtonPressed,
                    OnGamepadYButtonPressed,
                    OnGamepadMenuButtonPressed,
                    OnGamepadViewButtonPressed,
                    onNoFocusDetected: OnGamepadNoFocusDetected
                );
            }
            _gamepadNavigationService.Start();
        }

        /// <summary>
        /// GamepadNavigationService 偵測到「按下 D-pad / 左類比搖桿當下完全沒有元件持有焦點」時呼叫。
        /// 自我修復用最後防線：不管沒有初始焦點的確切成因為何，直接依目前分頁補上第一個可用控制項的焦點。
        /// </summary>
        private void OnGamepadNoFocusDetected()
        {
            DebugLogger.Log($"[SettingsPage] OnGamepadNoFocusDetected() called, _currentNavTag={_currentNavTag}, IsGamepadMappingEditorVisible={IsGamepadMappingEditorVisible}");
            if (_currentNavTag == "GamepadMapping")
            {
                if (IsGamepadMappingEditorVisible) GamepadProfileEditor.FocusFirstControl();
                else GamepadProfileList.FocusList();
            }
            else
            {
                FocusFirstElementForPage(_currentNavTag);
            }
        }

        /// <summary>
        /// 停止 Xbox 手把的輸入輪詢機制。
        /// 於結束應用程式或離開設定介面時呼叫。
        /// </summary>
        public void StopGamepadPolling()
        {
            _gamepadNavigationService?.Stop();
        }

        /// <summary>
        /// 釋放手把導覽服務的計時器與系統級資源。應用程式結束前呼叫。
        /// </summary>
        public void DisposeGamepadService()
        {
            _gamepadNavigationService?.Dispose();
            _gamepadNavigationService = null;
        }

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式（設定介面）。
        /// 依焦點所在元素分派：GridViewItem 選取平台、NavigationViewItem 切換頁面、各控制項觸發對應操作。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);

            // 手把映射分頁有自己的 A 鍵語意：焦點在內容區時才走專屬邏輯，
            // 焦點在左側 NavigationView / 漢堡 / 返回鈕 時讓 switch 走預設處理(切 NavigationView / 開合 pane)
            if (_currentNavTag == "GamepadMapping" &&
                focused is DependencyObject focusedDep && IsDescendantOf(focusedDep, GamepadMappingPage))
            {
                if (IsGamepadMappingEditorVisible)
                {
                    GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);
                    return;
                }
                // 清單頁：焦點落在 ListView / 列項時呼叫 EditSelected，垃圾桶 Button 走一般觸發
                if (focused is ListView || focused is ListViewItem)
                {
                    GamepadProfileList.EditSelected();
                    return;
                }
                GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);
                return;
            }

            switch (focused)
            {
                // 平台卡片：確認選取（不可用卡片不處理）。賦值 SelectedItem 會觸發
                // PlatformGridView_SelectionChanged，由該處統一處理 _selectedPlatformId 與新增動作卡的分派。
                case GridViewItem { Content: PlatformCardItem { IsAvailable: true } card }:
                    PlatformGridView.SelectedItem = card;
                    break;

                // 設定導覽項目（一般 / 進階 / 疑難排解）：選取頁面並收合側邊欄
                case NavigationViewItem navItem:
                    SettingsNav.SelectedItem = navItem;
                    SettingsNav.IsPaneOpen = false;
                    break;

                // NavigationView 內建返回按鈕：無操作
                case Button { Name: "NavigationViewBackButton" }:
                    break;

                // 漢堡選單按鈕：切換側邊欄展開 / 收合狀態
                case FrameworkElement { Name: "TogglePaneButton" }:
                    SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
                    break;

                // 重設 Game Bar 按鈕：觸發殺行程並重新啟動 FSE 的備援流程
                case Button btn when ReferenceEquals(btn, ResetGameBarButton):
                    ResetGameBarButton_Click(this, new RoutedEventArgs());
                    break;

                // PhantomKey 手把輸入開關 — 已移除（FSE 常駐），保留註解以利復原
                //case ToggleSwitch sw when ReferenceEquals(sw, UsePhantomKeySwitch):
                //    UsePhantomKeySwitch.IsOn = !sw.IsOn;
                //    break;

                // Steam In-Game Overlay 開關
                case ToggleSwitch sw when ReferenceEquals(sw, UsePhantomKeySteamInGameOverlaySwitch):
                    UsePhantomKeySteamInGameOverlaySwitch.IsOn = !sw.IsOn;
                    break;

                // [MOVED] Mouse Mode 主開關移至 OmniNav 頁（GamepadProfileListView），其手把 A 鍵切換在該頁處理。
                // case ToggleSwitch sw when ReferenceEquals(sw, MouseModeSwitch):
                //     if (sw.IsEnabled) MouseModeSwitch.IsOn = !sw.IsOn;
                //     break;

                // 導覽音效開關
                case ToggleSwitch sw when ReferenceEquals(sw, NavigationSoundsSwitch):
                    NavigationSoundsSwitch.IsOn = !sw.IsOn;
                    break;

                // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），手把 A 鍵切換 case 一併停用。
                //
                // // Game Bar 媒體櫃開關：On = 媒體櫃按鈕開啟 OmniConsole 設定；Off = 開啟預設平台
                // case ToggleSwitch sw when ReferenceEquals(sw, UseGameBarLibrarySwitch):
                //     UseGameBarLibrarySwitch.IsOn = !sw.IsOn;
                //     break;
                //
                // // Passthrough 開關：切換「首頁 / 媒體櫃按鈕直接導向預設平台，跳過 OmniConsole」
                // case ToggleSwitch sw when ReferenceEquals(sw, EnablePassthroughSwitch):
                //     EnablePassthroughSwitch.IsOn = !sw.IsOn;
                //     break;

                // 自動檢查更新開關
                case ToggleSwitch sw when ReferenceEquals(sw, AutoUpdateCheckSwitch):
                    AutoUpdateCheckSwitch.IsOn = !sw.IsOn;
                    break;

                // 除錯日誌開關
                case ToggleSwitch sw when ReferenceEquals(sw, DebugLoggingSwitch):
                    DebugLoggingSwitch.IsOn = !sw.IsOn;
                    break;

                // 開啟記錄檔資料夾按鈕
                case Button btn when ReferenceEquals(btn, OpenLogFolderButton):
                    OpenLogFolderButton_Click(this, new RoutedEventArgs());
                    break;

                // 開機影片開關
                case ToggleSwitch sw when ReferenceEquals(sw, BootVideoSwitch):
                    BootVideoSwitch.IsOn = !sw.IsOn;
                    break;

                // 開機影片「選擇影片」按鈕
                case Button btn when ReferenceEquals(btn, BootVideoChooseButton):
                    BootVideoChooseButton_Click(this, new RoutedEventArgs());
                    break;

                // 開機影片與平台啟動先後順序開關
                case ToggleSwitch sw when ReferenceEquals(sw, BootVideoSyncSwitch):
                    BootVideoSyncSwitch.IsOn = !sw.IsOn;
                    break;

                // 檢查更新按鈕
                case Button btn when ReferenceEquals(btn, CheckForUpdatesButton):
                    CheckForUpdatesButton_Click(this, new RoutedEventArgs());
                    break;

                // 下載並安裝按鈕
                case Button btn when ReferenceEquals(btn, DownloadInstallButton):
                    DownloadInstallButton_Click(this, new RoutedEventArgs());
                    break;

                // 開發人員模式設定按鈕
                case HyperlinkButton btn when ReferenceEquals(btn, DeveloperModeOpenSettingsButton):
                    DeveloperModeOpenSettings_Click(this, new RoutedEventArgs());
                    break;

                // 關於頁「複製到剪貼簿」按鈕
                case Button btn when ReferenceEquals(btn, CopyAboutButton):
                    CopyAboutButton_Click(this, new RoutedEventArgs());
                    break;

                // 關於頁「重新整理」按鈕
                case Button btn when ReferenceEquals(btn, RefreshAboutButton):
                    RefreshAboutButton_Click(this, new RoutedEventArgs());
                    break;
            }
        }

        /// <summary>
        /// 處理手把 'B' 鍵被按下的回呼函式。
        /// 導覽選單展開時先收合，否則觸發全域退出。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            // 手把映射編輯器頁的 B 鍵 = 儲存並返回
            if (TryHandleGamepadMappingBackKey()) return;

            if (SettingsNav.IsPaneOpen)
            {
                SettingsNav.IsPaneOpen = false;
                return;
            }

            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 手把 Y 鍵：General 頁一律觸發匯入自訂平台；GamepadMapping 清單頁將焦點 profile 設為預設。
        /// </summary>
        private void OnGamepadYButtonPressed()
        {
            // 手把映射清單頁的 Y 鍵 = 將焦點 profile 設為預設
            // (New profile 改為僅可由清單下方的按鈕觸發)
            if (_currentNavTag == "GamepadMapping")
            {
                if (IsGamepadMappingListVisible) GamepadProfileList.SetSelectedAsDefault();
                return;
            }
            if (_currentNavTag != "General") return;
            ImportHintButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 手把 X 鍵：觸發編輯目前聚焦的自訂平台（系統平台／新增動作卡無作用）。
        /// </summary>
        private void OnGamepadXButtonPressed()
        {
            // 手把映射分頁的 X 鍵 = 刪除（清單頁刪選中項；編輯器頁刪目前 profile）
            if (TryHandleGamepadMappingDeleteKey()) return;
            if (_currentNavTag != "General") return;

            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem { Content: PlatformCardItem { IsCustom: true } card })
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>
        /// 底部提示列「Menu 啟動」按鈕的滑鼠點選處理。
        /// </summary>
        private void LaunchPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            OnGamepadMenuButtonPressed();
        }

        /// <summary>
        /// 手把 Menu（☰）鍵：直接啟動目前聚焦（或已選取）的平台，跳過手動 FSE 切換流程。僅在 FSE 模式中有效。
        /// 若焦點在可用的平台卡片上，先將其設為選取（同 A 鍵），再通知 MainWindow 啟動。
        /// </summary>
        private void OnGamepadMenuButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (!FseService.IsActive()) return;

            // 若焦點在可用平台卡片上，先確認選取（更新預設平台）；「新增」動作卡不是可啟動的平台
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem { Content: PlatformCardItem { IsAvailable: true, IsAddNewCard: false } card })
            {
                PlatformGridView.SelectedItem = card;
                _selectedPlatformId = card.Id;
            }

            if (string.IsNullOrEmpty(_selectedPlatformId)) return;

            LaunchPlatformDirectlyRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 側邊選單開合 ───────────────────────────────────────────────────────

        /// <summary>
        /// 手把 View（⊞）鍵：開合側邊選單。選單僅能由此鍵開啟，
        /// 漢堡按鈕不參與控制器導覽（見 SettingsNav_Loaded）。
        /// </summary>
        private void OnGamepadViewButtonPressed()
        {
            SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
        }

        /// <summary>底部提示列「開啟選單」按鈕的滑鼠點選處理。</summary>
        private void ViewButtonHintButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsNav.IsPaneOpen = true;
        }

        /// <summary>側邊選單展開：導覽項目恢復可聚焦，並把焦點移到目前選取項目。</summary>
        private void SettingsNav_PaneOpened(NavigationView sender, object args)
        {
            UpdateNavItemFocusability(true);
            DispatcherQueue.TryEnqueue(() =>
                (SettingsNav.SelectedItem as NavigationViewItem)?.Focus(FocusState.Programmatic));
        }

        /// <summary>側邊選單收合：導覽項目不可聚焦，避免控制器從內容區跳進選單。</summary>
        private void SettingsNav_PaneClosed(NavigationView sender, object args)
        {
            UpdateNavItemFocusability(false);
        }

        /// <summary>
        /// SettingsNav 載入完成：套用初始可聚焦狀態（pane 預設收合），
        /// 並關閉漢堡按鈕的 Tab 停駐，使其無法被控制器導覽自內容區聚焦。
        /// 滑鼠／觸控仍可點選漢堡按鈕。
        /// </summary>
        private void SettingsNav_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateNavItemFocusability(SettingsNav.IsPaneOpen);

            if (FindDescendantByName(SettingsNav, "TogglePaneButton") is Control toggleButton)
                toggleButton.IsTabStop = false;
        }

        /// <summary>依 pane 開合狀態切換導覽項目的控制器可聚焦性。</summary>
        private void UpdateNavItemFocusability(bool paneOpen)
        {
            foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
                item.IsTabStop = paneOpen;
            foreach (var item in SettingsNav.FooterMenuItems.OfType<NavigationViewItem>())
                item.IsTabStop = paneOpen;
        }

        /// <summary>在視覺樹中依名稱往下搜尋第一個符合的子元素。</summary>
        private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Name == name)
                    return child;
                if (FindDescendantByName(child, name) is { } nested)
                    return nested;
            }
            return null;
        }

        // ── 更新檢查 ───────────────────────────────────────────────────────────

        /// <summary>自動更新檢查開關切換。</summary>
        private void AutoUpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetAutoUpdateCheckEnabled(AutoUpdateCheckSwitch.IsOn);
            ShowSettingsUpdateInfoBar();
        }

        /// <summary>手動檢查更新按鈕，強制抓取 GitHub API 並更新快取。</summary>
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate) return;
            _isCheckingUpdate = true;

            CheckDeveloperMode(); // 使用者可能從設定頁回來後狀態已變更
            UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateCheck_Checking");
            UpdateCheckStatusText.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.IsActive = true;

            var delayTask = Task.Delay(500);
            var (kind, version) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();
            await delayTask;

            CheckUpdateProgressRing.IsActive = false;
            CheckUpdateProgressRing.Visibility = Visibility.Collapsed;
            _isCheckingUpdate = false;

            switch (kind)
            {
                case UpdateCheckService.UpdateKind.MissingPhantomLink:
                    UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    ShowSettingsUpdateInfoBar();
                    break;

                case UpdateCheckService.UpdateKind.MainAppUpdate:
                    UpdateCheckStatusText.Text = string.Format(
                        _resourceLoader.GetString("UpdateCheck_NewVersion_Subtitle"), version);
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    ShowSettingsUpdateInfoBar();
                    break;

                case UpdateCheckService.UpdateKind.None:
                    UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateCheck_UpToDate_Subtitle");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Collapsed;
                    SettingsUpdateInfoBar.IsOpen = false;
                    break;
            }
        }

        /// <summary>下載並安裝更新按鈕（PhantomLink 先裝，OmniConsole 後裝）。</summary>
        private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadCts != null) return; // 下載中，防止重複觸發

            // 點選時再次確認開發人員模式，防止使用者中途關閉
            CheckDeveloperMode();
            if (!UpdateCheckService.IsDeveloperModeEnabled()) return;

            // 重新檢查最新版本，確保下載的是最新的而非過期快取
            var (kind, _) = await UpdateCheckService.CheckForUpdateAsync();

            var mainUrl = SettingsService.GetCachedDownloadUrl();
            var phantomLinkUrl = SettingsService.GetCachedPhantomLinkUrl();
            var targetVersion = SettingsService.GetCachedNewVersion();

            if (string.IsNullOrEmpty(mainUrl) && string.IsNullOrEmpty(phantomLinkUrl))
            {
                // 無快取下載連結時回退開瀏覽器
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri(UpdateCheckService.ReleaseNotesUrl));
                return;
            }

            bool mainSkippable = kind == UpdateCheckService.UpdateKind.MissingPhantomLink
                && targetVersion == SettingsService.GetAppVersion();

            await RunInstallBundleWithDialogAsync(phantomLinkUrl, mainUrl, targetVersion,
                mainSkippable, resumeFromPhase2: false);
        }

        /// <summary>
        /// 將 InstallBundleAsync 包進 UpdateProgressDialog，由對話方塊以模態方式擋住手把 B 鍵與 Esc，
        /// 並在 MainWindow 端攔截視窗關閉。失敗後解除鎖定並顯示失敗訊息於原 InfoBar。
        /// </summary>
        internal async Task RunInstallBundleWithDialogAsync(
            string phantomLinkUrl, string mainUrl, string targetVersion,
            bool mainSkippable, bool resumeFromPhase2)
        {
            var dialog = new UpdateProgressDialog(this.XamlRoot, _resourceLoader);

            try
            {
                _downloadCts = new CancellationTokenSource();
                var progress = new Progress<double>(pct => dialog.ReportProgress(pct));
                var status = new Progress<string>(key =>
                {
                    dialog.ReportStatus(key);
                    // Phase 2 末端兩條路徑（實際安裝 / mainSkippable 重啟）會由 OS 送 graceful close
                    // 請求給本視窗，此時鬆開 AppWindow.Closing 鎖讓請求通過
                    if (key == "Phase2Install" || key == "Phase2RequestingRestart")
                        MainWindow.IsUpdateInstallInProgress = false;
                });

                // 終止 PhantomKey，避免 MSIX 更新時因 .exe 佔用而拖慢進度
                PhantomKeyService.Kill();

                // MSIX 更新前取消 FSE 狀態通知
                FseService.StopListening();

                // 註冊自動重啟，ForceApplicationShutdown 結束 OmniConsole 後 Windows 會自動重新啟動 OmniConsole
                RegisterApplicationRestart("", 0);

                // 設定安裝鎖定旗標供 MainWindow 讀取
                MainWindow.IsUpdateInstallInProgress = true;

                // 對話方塊期間停掉設定頁的手把輪詢
                StopGamepadPolling();

                // 不 await ShowAsync，與 InstallBundleAsync 並行執行
                var showTask = dialog.ShowAsync().AsTask();

                await UpdateCheckService.InstallBundleAsync(
                    phantomLinkUrl, mainUrl, targetVersion,
                    mainSkippable, resumeFromPhase2,
                    progress, status, _downloadCts.Token);

                // ForceApplicationShutdown / RequestRestartAsync 路徑會結束本行程，此後程式碼為回退路徑
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugLogger.Log($"[SettingsPage] Download/install failed: {ex.Message}");
                dialog.RequestClose();
                MainWindow.IsUpdateInstallInProgress = false;
                StartGamepadPolling();
                UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateDownload_Failed");
                UpdateCheckStatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        /// <summary>
        /// 自動檢查更新（靜默，有動作時顯示 InfoBar）。
        /// </summary>
        private async Task AutoCheckForUpdatesAsync()
        {
            var (kind, _) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();

            if (kind != UpdateCheckService.UpdateKind.None)
            {
                ShowSettingsUpdateInfoBar();
                ShowCachedUpdateStatus();
            }
        }

        /// <summary>
        /// 依快取的 UpdateKind 顯示或隱藏設定頁 InfoBar。
        /// </summary>
        private void ShowSettingsUpdateInfoBar()
        {
            if (!SettingsService.GetAutoUpdateCheckEnabled())
            {
                SettingsUpdateInfoBar.IsOpen = false;
                return;
            }

            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                SettingsUpdateInfoBar.Title = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                SettingsUpdateInfoBar.Message = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Message");
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                SettingsUpdateInfoBar.Title = "";
                SettingsUpdateInfoBar.Message = string.Format(
                    _resourceLoader.GetString("UpdateAvailable_InfoBar_Settings"), cached);
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else
            {
                SettingsUpdateInfoBar.IsOpen = false;
            }
        }

        /// <summary>
        /// 依快取的 UpdateKind，在版本號下方顯示狀態文字與「下載並安裝」按鈕。
        /// </summary>
        private void ShowCachedUpdateStatus()
        {
            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = string.Format(
                    _resourceLoader.GetString("UpdateCheck_NewVersion_Subtitle"), cached);
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateCheckStatusText.Visibility = Visibility.Collapsed;
                DownloadInstallButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 檢查開發人員模式是否啟用，未啟用時顯示黃色警告並停用下載按鈕。
        /// </summary>
        private void CheckDeveloperMode()
        {
            bool enabled = UpdateCheckService.IsDeveloperModeEnabled();
            DeveloperModeWarningPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            if (!enabled)
                DeveloperModeWarningText.Text = _resourceLoader.GetString("DeveloperMode_Warning");
            DownloadInstallButton.IsEnabled = enabled;
        }

        /// <summary>開啟 Windows 開發人員模式設定頁面。</summary>
        private async void DeveloperModeOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
        }
    }
}
