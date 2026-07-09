using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 啟動畫面 UserControl。
    /// 負責平台自動啟動、FSE 引導畫面及啟動失敗時的操作按鈕。
    /// </summary>
    public sealed partial class LaunchPage : UserControl
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_HIDE = 0;

        // 開機影片「與平台同時啟動」模式用：播放期間把自己釘在 Z-order 最上層，避免平台視窗
        // 一建立就蓋掉還在播的影片（Win32 的 Z-order 領先於 GetForegroundWindow() 回報的啟動狀態，
        // 見 LaunchDefaultPlatformAsync 內的說明）。
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>啟動失敗或需要進行設定時，由 MainWindow 切換至設定介面。</summary>
        public event EventHandler? NavigateToSettingsRequested;

        /// <summary>使用者點選「返回桌面」或手把 B 鍵時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 WS_EX_TOOLWINDOW 設定使用。</summary>
        public IntPtr Hwnd { get; set; }

        /// <summary>
        /// 已完成過一次實際啟動嘗試。
        /// MainWindow_Activated 在此為 true 時不再重複觸發啟動，避免視窗重新取得焦點後再次啟動。
        /// </summary>
        public bool HasLaunchedOnce => _hasLaunchedOnce;

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private readonly ResourceLoader _resourceLoader = new();

        private GamepadNavigationService? _launchPanelGamepadService;

        private MediaPlayer? _bootVideoPlayer;
        private bool _bootVideoPlaying;

        public LaunchPage()
        {
            InitializeComponent();
            this.KeyDown += LaunchPage_KeyDown;
        }

        // ── 平台啟動 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 先預檢可用性，不可用則顯示錯誤訊息；啟動成功後隱藏視窗，
        /// 輪詢前景視窗確認平台已到前景後結束應用程式。
        /// </summary>
        /// <param name="suppressBootVideo">
        /// true 時略過開機影片。用於 Reactivate（Game Bar「Home」）路徑——該路徑無論平台是否
        /// 仍在執行都會強制重跑一次啟動流程（既有既定行為，見 MainWindow.Reactivate 註解），
        /// 若平台其實還在前景執行，此次「重新啟動」通常只是把既有視窗喚回前景、幾乎瞬間完成，
        /// 這時仍強制播放開機影片會是純粹的干擾（畫面被蓋住好幾秒，使用者卻什麼都沒離開過）。
        /// </param>
        public async Task LaunchDefaultPlatformAsync(bool suppressBootVideo = false)
        {
            if (_isLaunching) return;

            // 首次執行或版本更新時不自動啟動，轉至設定介面讓使用者確認預設平台
            if (SettingsService.IsFirstRunOrUpdate())
            {
                NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _isLaunching = true;

            try
            {
                // 重設為初始狀態，確保上次失敗殘留的按鈕等元素被收合
                // 注意：SettingsPage 的可見性由呼叫方 MainWindow 在進入此方法前已處理
                VisualStateManager.GoToState(this, "Idle", false);

                // 讀取快取的更新資訊，有新版時顯示 InfoBar
                ShowUpdateInfoBarIfNeeded();

                StartGamepadPolling();

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                // 預檢平台可用性，不可用則直接顯示訊息，避免無謂的啟動嘗試與逾時等待
                if (!await ProcessLauncherService.CheckPlatformAvailableAsync(platform))
                {
                    StatusText.Text = string.Format(_resourceLoader.GetString("PlatformNotAvailable"), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                    return;
                }

                // 開機影片：啟用且已匯入有效檔案時播放。與平台啟動的先後順序由使用者在設定頁選擇：
                //   - Sync（預設，playBeforeLaunch=true）：先把影片整支播完，才呼叫 LaunchPlatformAsync。
                //     最保險，影片保證看得到，代價是總開機時間 = 影片長度 + 平台啟動時間。
                //   - Async（playBeforeLaunch=false）：影片開始播放後不等它，立刻接著呼叫
                //     LaunchPlatformAsync 讓平台同時在背景載入，兩者同時起步。實測發現：平台視窗一
                //     建立，即使 GetForegroundWindow() 都還沒變成它，Z-order 也可能已經蓋過我們的
                //     影片畫面（Win32 的 Z-order 領先於「取得前景/啟動」這個語意）——所以這個模式下
                //     播放期間會把自己釘在 Z-order 最上層（HWND_TOPMOST），播完才放開，確保使用者
                //     全程看到的是影片，平台則已經在背後備妥，播完的瞬間無縫接軌。
                //
                // IsPlatformRunning：Game Bar「Home」鍵在平台仍在前景執行時，會讓 OmniConsole 冷啟動
                // 一個全新行程（因為每次成功啟動平台後 OmniConsole 都會整個結束退出），suppressBootVideo
                // 這個既有旗標只在「重導到還活著的既有實例」（Reactivate）才會是 true，涵蓋不到這個
                // 冷啟動的情境。改直接檢查平台的執行檔是否已經在跑，跑著就不算是「開機」，略過影片。
                bool bootVideoEnabled = SettingsService.GetEnableBootVideo();
                bool platformAlreadyRunning = ProcessLauncherService.IsPlatformRunning(platform);
                bool willPlayBootVideo = !suppressBootVideo && bootVideoEnabled && !platformAlreadyRunning;
                bool playBeforeLaunch = SettingsService.GetBootVideoPlayBeforeLaunch();
                DebugLogger.Log($"[LaunchPage] willPlayBootVideo={willPlayBootVideo} playBeforeLaunch={playBeforeLaunch} (suppressBootVideo={suppressBootVideo}, enabled={bootVideoEnabled}, alreadyRunning={platformAlreadyRunning})");

                // 顯示平台圖示與進度指示——要播影片時改切到 LaunchingWithVideo 狀態，而不是切到
                // Launching 再用程式碼覆寫 Visibility：LaunchIconBorder / LaunchProgressRing 在
                // Launching 狀態下已被 VisualState.Setters 接管，事後用程式碼直接賦值會跟 Setter
                // 打架（實測會設不掉，見 LaunchPage.xaml 內 LaunchingWithVideo 狀態註解）。
                VisualStateManager.GoToState(this, willPlayBootVideo ? "LaunchingWithVideo" : "Launching", false);
                LaunchIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(platform.IconAsset));

                bool isTopmostForVideo = false;
                Task bootVideoTask;
                if (willPlayBootVideo && playBeforeLaunch)
                {
                    // Sync：先把影片播完，這裡直接 await，下面的 bootVideoTask 就不用再等了。
                    await StartBootVideoAsync();
                    bootVideoTask = Task.CompletedTask;
                }
                else if (willPlayBootVideo)
                {
                    // Async：先釘 Topmost 再起步播放，兩者同時開始。
                    SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    isTopmostForVideo = true;
                    bootVideoTask = StartBootVideoAsync();
                }
                else
                {
                    bootVideoTask = Task.CompletedTask;
                }

                StatusText.Text = string.Format(_resourceLoader.GetString("Launching"), platformName);

                bool isTimeout = false;
                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 給予足夠的逾時時間來確保平台順利到前景，避免 FSE 重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    int exStyle = GetWindowLong(Hwnd, GWL_EXSTYLE);
                    SetWindowLong(Hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // [Windows Bug] 部分應用程式在 FSE 中會被最大化並搶走前景焦點，
                    // 在輪詢前先終止，避免干擾前景判定。
                    // 從 OmniConsole 進入 FSE 時已在 App.xaml.cs 預先清理，
                    // 但 Win+F11、工作檢視、開機自動進入等路徑不經過該清理，仍需此防禦。
                    FseService.KillIgnoredBackgroundServices();

                    // 影片跟平台啟動是同時起步的，這裡先等影片播完（若沒在播則立刻返回），讓平台有
                    // 機會在影片播放期間於背景把畫面準備好，達成「影片播完 → 直接看到平台畫面」的效果。
                    await bootVideoTask;
                    if (isTopmostForVideo)
                    {
                        SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        isTopmostForVideo = false;
                    }

                    // 輪詢前景視窗：一旦前景不再是 OmniConsole，代表平台已到前景，可以安全退出
                    // 超過 slowWarningSeconds 顯示緩和提示，超過 timeoutSeconds 進入失敗流程
                    const int pollIntervalMs = 500;
                    const int slowWarningSeconds = 15;
                    const int timeoutSeconds = 60;

                    bool platformToForeground = false;
                    int elapsed = 0;

                    while (elapsed < timeoutSeconds * 1000)
                    {
                        await Task.Delay(pollIntervalMs);
                        elapsed += pollIntervalMs;
                        IntPtr fg = GetForegroundWindow();
                        if (fg != Hwnd)
                        {
                            if (FseService.IsIgnoredForegroundWindow(fg))
                                continue;
                            platformToForeground = true;
                            break;
                        }
                        if (elapsed == slowWarningSeconds * 1000)
                            VisualStateManager.GoToState(this, "LaunchingSlow", false);
                    }

                    if (platformToForeground)
                    {
                        StopBootVideo();

                        // FSE 環境下啟動 PhantomKey 手把輸入服務（常駐，不再檢查使用者開關）
                        //if (FseService.IsActive() && SettingsService.GetUsePhantomKey())
                        if (FseService.IsActive())
                            PhantomKeyService.Start();

                        ShowWindow(Hwnd, SW_HIDE);
                        App.ExitApp();
                        return;
                    }

                    // 若逾時仍未取得前景，還原視窗狀態並進入失敗流程
                    SetWindowLong(Hwnd, GWL_EXSTYLE, exStyle);
                    success = false;
                    isTimeout = true;
                }

                if (!success)
                {
                    // LaunchPlatformAsync 本身失敗（尚未進到 if (success) 內）時，影片可能還在播——
                    // 直接跳過並等它收尾完成，避免它事後才把畫面切回 Launching，蓋掉這裡要顯示的
                    // LaunchError 狀態。isTimeout 路徑不受影響：那條路徑在進 if (success) 時就已經
                    // await 過 bootVideoTask，此時影片必定已經播完，_bootVideoPlaying 一定是 false。
                    if (_bootVideoPlaying)
                    {
                        _bootVideoSkipTcs?.TrySetResult(true);
                        await bootVideoTask;
                    }

                    // 啟動失敗：切換至 LaunchError 狀態（VSM 負責隱藏圖示/進度圈，顯示操作按鈕）
                    string errorStringKey = isTimeout ? "LaunchTimeout" : "LaunchFailed";
                    StatusText.Text = string.Format(_resourceLoader.GetString(errorStringKey), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                }
            }
            finally
            {
                StopBootVideo();
                // 保底：不管哪個分支結束，只要開頭有為了 Async 開機影片釘過 Topmost，這裡一律解除，
                // 避免任何未預期的例外路徑讓視窗卡在 Topmost 狀態。NOTOPMOST 呼叫本身是幂等的，
                // 就算根本沒釘過也無害，不需要額外判斷條件。
                SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                _isLaunching = false;
            }
        }

        // ── 開機影片 ──────────────────────────────────────────────────────────
        //
        // 設計取捨（相對於 AnyFSE 參考實作，見對話中的調查結論）：
        // AnyFSE 用「獨立的 topmost 啟動畫面視窗」播放影片，得跟目標平台自己的視窗搶前景/
        // 啟動焦點，且用鬆散的視窗輪詢啟發式判斷「平台是否已啟動」，兩者都是不穩定的根源。
        // 這裡改把影片當成 LaunchPage（本來就是啟動流程唯一、一路持有前景的視窗）內的一層
        // 畫面，沿用既有、已測試過的前景輪詢迴圈（LaunchDefaultPlatformAsync 內）判斷
        // 「平台是否已到前景」，不需要另外發明偵測機制，也不會有兩個視窗互搶前景的問題。
        // 播放失敗或使用者中途跳過時一律安靜退回原本的圖示 + 進度圈畫面，絕不卡住或黑畫面。

        // 解碼逾時：避免因檔案損毀等原因卡住不動，逾時就放棄播放、直接繼續啟動流程。
        private static readonly TimeSpan BootVideoOpenTimeout = TimeSpan.FromSeconds(5);
        // 播放逾時保底：影片不循環、只播一次自然結束（MediaEnded），此為防呆上限——
        // 萬一影片檔本身時長標錯或 MediaEnded 事件異常沒觸發，最多等這麼久就強制往下走，
        // 不讓開機流程被一支放不完的影片卡死。
        private static readonly TimeSpan BootVideoMaxDuration = TimeSpan.FromSeconds(30);

        private TaskCompletionSource<bool>? _bootVideoSkipTcs;

        /// <summary>
        /// 啟動開機影片播放（不循環，播一次自然結束），並立刻返回一個代表「影片播放已經結束」
        /// （自然播完 / 被使用者跳過 / 解碼失敗逾時 / 防呆逾時）的 Task——呼叫端不必等這個 Task
        /// 就能接著做其它事（例如同時呼叫 LaunchPlatformAsync 讓平台在背景開始載入），等真正需要
        /// 「影片播完了嗎」的答案時才另外 await 這個 Task。這樣影片會播滿全長，不會被平台視窗
        /// 提前到前景蓋掉（見本檔案開頭「開機影片」設計說明）。未啟用、未匯入檔案時不應呼叫本方法
        /// （由呼叫端的 willPlayBootVideo 判斷決定）。
        /// </summary>
        private async Task StartBootVideoAsync()
        {
            string? path = BootVideoStore.GetVideoFilePath();
            DebugLogger.Log($"[LaunchPage] StartBootVideoAsync: path={path ?? "null"}");
            if (string.IsNullOrEmpty(path))
            {
                VisualStateManager.GoToState(this, "Launching", false);
                return;
            }

            var mediaOpenedTcs = new TaskCompletionSource<bool>();
            var mediaEndedTcs = new TaskCompletionSource<bool>();
            _bootVideoSkipTcs = new TaskCompletionSource<bool>();

            try
            {
                var player = new MediaPlayer
                {
                    IsLoopingEnabled = false,
                    IsMuted = true,
                };
                player.MediaFailed += (s, args) =>
                {
                    DebugLogger.Log($"[LaunchPage] Boot video MediaFailed: {args.Error} {args.ErrorMessage}");
                    mediaOpenedTcs.TrySetResult(false);
                };
                player.MediaOpened += (s, args) =>
                {
                    DebugLogger.Log($"[LaunchPage] Boot video MediaOpened: duration={s.PlaybackSession?.NaturalDuration}");
                    mediaOpenedTcs.TrySetResult(true);
                };
                player.MediaEnded += (s, args) =>
                {
                    DebugLogger.Log("[LaunchPage] Boot video MediaEnded");
                    mediaEndedTcs.TrySetResult(true);
                };
                player.Source = MediaSource.CreateFromUri(new Uri(path));

                _bootVideoPlayer = player;
                BootVideoPlayer.SetMediaPlayer(player);
                BootVideoPlayer.Visibility = Visibility.Visible;
                // 圖示卡片/狀態文字/進度圈/浮水印已由呼叫端（LaunchDefaultPlatformAsync）在呼叫本方法
                // 之前、緊接 VSM 切到 LaunchingWithVideo 就同步收起，這裡不用重複處理。
                _bootVideoPlaying = true;
                player.Play();

                var openedWinner = await Task.WhenAny(
                    mediaOpenedTcs.Task, Task.Delay(BootVideoOpenTimeout), _bootVideoSkipTcs.Task);
                bool opened = openedWinner == mediaOpenedTcs.Task && await mediaOpenedTcs.Task;
                DebugLogger.Log($"[LaunchPage] StartBootVideoAsync: opened={opened} (winner={(openedWinner == _bootVideoSkipTcs.Task ? "skip" : openedWinner == mediaOpenedTcs.Task ? "mediaOpened" : "timeout")})");
                if (!opened)
                {
                    // 解碼失敗/逾時，或使用者在解碼完成前就按了跳過：這種情況下使用者從頭到尾什麼
                    // 畫面都還沒看到過，退回一般圖示 + 進度圈畫面，至少有東西可看、不留一片死黑。
                    StopBootVideo();
                    VisualStateManager.GoToState(this, "Launching", false);
                    return;
                }

                var endedWinner = await Task.WhenAny(
                    mediaEndedTcs.Task, Task.Delay(BootVideoMaxDuration), _bootVideoSkipTcs.Task);
                DebugLogger.Log($"[LaunchPage] StartBootVideoAsync: playback finished (winner={(endedWinner == _bootVideoSkipTcs.Task ? "skip" : endedWinner == mediaEndedTcs.Task ? "mediaEnded" : "maxDurationTimeout")})");

                // 影片已經正常播放（自然播完 / 使用者跳過 / 防呆逾時，三者皆已讓使用者看過開機影片
                // 這段體驗），刻意不切回 "Launching"：LaunchingWithVideo 狀態下全部元件本來就是
                // Collapsed，此時只把 BootVideoPlayer 本身收掉即可，留一片純黑畫面撐到平台視窗真的
                // 出現、視窗被隱藏為止——不讓使用者在影片結束後又看到一閃而過的圖示卡片/進度圈/文字，
                // 那樣反而會覺得畫面在「閃爍」。真正的圖示卡片/進度圈只保留給「根本沒放過影片」時
                // 當唯一的視覺回饋，或是啟動異常慢（LaunchingSlow）時才需要蓋過去提醒使用者。
                StopBootVideo();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[LaunchPage] StartBootVideoAsync failed: {ex.Message}");
                StopBootVideo();
                VisualStateManager.GoToState(this, "Launching", false);
            }
        }

        /// <summary>
        /// 停止開機影片播放並釋放播放器資源，隱藏 BootVideoPlayer 本身。可重複呼叫（已停止時為
        /// no-op）。刻意不在這裡處理 LaunchIconBorder / StatusText 等其它元件的可見度還原——
        /// 那些是交給 VisualStateManager 切回 "Launching" 狀態處理（見 LaunchPage.xaml 內
        /// LaunchingWithVideo 狀態的註解：事後用程式碼直接改 Visibility 會跟 VisualState.Setters
        /// 打架）。呼叫端該不該切回 "Launching"，取決於當下是否還停留在播影片這個階段：
        ///   - StartBootVideoAsync 內解碼失敗/逾時、SkipBootVideo：一定還在 LaunchingWithVideo，切回。
        ///   - 平台已到前景成功、或最外層 finally：可能已經切到 LaunchError 或即將整個結束，
        ///     這裡若還硬切回 Launching 反而會覆蓋掉正確的畫面，所以不切，只單純釋放播放器資源。
        /// </summary>
        private void StopBootVideo()
        {
            if (_bootVideoPlayer == null) return;
            DebugLogger.Log($"[LaunchPage] StopBootVideo: position={_bootVideoPlayer.PlaybackSession?.Position}");

            try
            {
                _bootVideoPlayer.Pause();
                BootVideoPlayer.SetMediaPlayer(null);
                _bootVideoPlayer.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[LaunchPage] StopBootVideo cleanup failed: {ex.Message}");
            }

            _bootVideoPlayer = null;
            _bootVideoPlaying = false;
            BootVideoPlayer.Visibility = Visibility.Collapsed;
        }

        /// <summary>使用者提前跳過開機影片：解除保底等待、停止播放並切回一般的圖示 + 進度圈畫面。</summary>
        private void SkipBootVideo()
        {
            _bootVideoSkipTcs?.TrySetResult(true);
            StopBootVideo();
            VisualStateManager.GoToState(this, "Launching", false);
        }

        /// <summary>鍵盤按任意鍵跳過開機影片（手把跳過見 OnLaunchPanelGamepadAButtonPressed）。</summary>
        private void LaunchPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!_bootVideoPlaying) return;
            SkipBootVideo();
            e.Handled = true;
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重設啟動狀態並重新啟動平台。
        /// 略過開機影片：這條路徑常見情境是平台其實還在前景執行、只是被 Home 鍵喚回，
        /// 此時「重新啟動」多半瞬間完成，播放開機影片只會是純粹的畫面干擾。
        /// </summary>
        public async void Reactivate()
        {
            _hasLaunchedOnce = false;
            await LaunchDefaultPlatformAsync(suppressBootVideo: true);
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示，引導使用者透過 XboxFullScreenExperienceTool 工具啟用。
        /// handheldRequired=true 時顯示「偵測到 PC 限制版、需要掌機完整版」訊息（IsSupported=true 但 DeviceForm≠46）。
        /// </summary>
        public void ShowFseNotAvailable(bool handheldRequired = false)
        {
            string resourceKey = handheldRequired ? "FseHandheldRequired" : "FseNotAvailable";
            DebugLogger.Log($"ShowFseNotAvailable: handheldRequired={handheldRequired}");
            StatusText.Text = _resourceLoader.GetString(resourceKey);
            VisualStateManager.GoToState(this, "FseNotAvailable", false);
            EnableFseButton.Focus(FocusState.Programmatic);
            StartGamepadPolling();
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示，只引導使用者至設定頁面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            DebugLogger.Log("ShowFseHomeAppNotSet: FSE Home App not set to OmniConsole.");
            StatusText.Text = _resourceLoader.GetString("FseHomeAppNotSet");
            VisualStateManager.GoToState(this, "FseHomeAppNotSet", false);
            OpenFseSettingsButton.Focus(FocusState.Programmatic);
            StartGamepadPolling();
        }

        // ── 按鈕事件處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// LaunchPanel 的「開啟設定」按鈕點選處理，切換至設定介面。
        /// 啟動失敗等情境均會顯示此按鈕。
        /// </summary>
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            StopGamepadPolling();
            VisualStateManager.GoToState(this, "Idle", false);
            NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// LaunchPanel 的「返回桌面」按鈕點選處理，觸發全域退出流程。
        /// 啟動失敗等情境均會顯示此按鈕。
        /// </summary>
        private void ReturnToDesktopButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 若 Xbox Full Screen Experience Tool 已安裝則直接啟動，否則開啟 GitHub 下載頁面。OmniConsole 保持開啟。
        /// </summary>
        private async void EnableFseButton_Click(object _, RoutedEventArgs __)
        {
            const string toolExePath = @"C:\Program Files\8bit2qubit\Xbox FullScreen Experience Tool\XboxFullScreenExperienceTool.exe";
            if (System.IO.File.Exists(toolExePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(toolExePath) { UseShellExecute = true });
            else
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri("https://github.com/8bit2qubit/XboxFullScreenExperienceTool"));
        }

        /// <summary>
        /// 開啟 Windows 設定中的全螢幕體驗頁面。
        /// </summary>
        private async void OpenFseSettingsButton_Click(object _, RoutedEventArgs __)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:gaming-fullscreen"));
        }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 手把輸入處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 LaunchPanel 的手把輪詢，使 A 鍵可觸發按鈕，B 鍵可退出。
        /// </summary>
        public void StartGamepadPolling()
        {
            _launchPanelGamepadService ??= new GamepadNavigationService(
                this.LaunchPanel,
                this.DispatcherQueue,
                OnLaunchPanelGamepadAButtonPressed,
                OnGamepadBButtonPressed
            );
            _launchPanelGamepadService.Start();
        }

        /// <summary>
        /// 停止 LaunchPanel 的手把輪詢。
        /// </summary>
        public void StopGamepadPolling()
        {
            _launchPanelGamepadService?.Stop();
        }

        /// <summary>
        /// 釋放手把導覽服務的計時器與系統級資源。應用程式結束前呼叫。
        /// </summary>
        public void DisposeGamepadService()
        {
            _launchPanelGamepadService?.Dispose();
            _launchPanelGamepadService = null;
        }

        /// <summary>
        /// LaunchPanel 中手把 'A' 鍵的處理：開機影片播放中時優先跳過；否則焦點在按鈕時觸發點選。
        /// </summary>
        private void OnLaunchPanelGamepadAButtonPressed()
        {
            if (_bootVideoPlaying)
            {
                SkipBootVideo();
                return;
            }

            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.XamlRoot);
            if (ReferenceEquals(focused, OpenSettingsButton))
                OpenSettingsButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, ReturnToDesktopButton))
                ReturnToDesktopButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, EnableFseButton))
                EnableFseButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, OpenFseSettingsButton))
                OpenFseSettingsButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 手把 'B' 鍵：觸發退出流程。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 更新通知 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 依快取的 UpdateKind 顯示 InfoBar（唯讀通知）。
        /// </summary>
        public void ShowUpdateInfoBarIfNeeded()
        {
            if (!SettingsService.GetAutoUpdateCheckEnabled())
            {
                UpdateInfoBar.IsOpen = false;
                return;
            }

            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                var plKey = SettingsService.GetUseGameBarLibraryForSettings()
                    ? "UpdateInfoBar_MissingPhantomLink_Launch_GameBar"
                    : "UpdateInfoBar_MissingPhantomLink_Launch_StartMenu";
                UpdateInfoBar.Message = _resourceLoader.GetString(plKey);
                UpdateInfoBar.IsOpen = true;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                var key = SettingsService.GetUseGameBarLibraryForSettings()
                    ? "UpdateAvailable_InfoBar_Launch_GameBar"
                    : "UpdateAvailable_InfoBar_Launch_StartMenu";
                UpdateInfoBar.Message = string.Format(
                    _resourceLoader.GetString(key), cached);
                UpdateInfoBar.IsOpen = true;
            }
            else
            {
                UpdateInfoBar.IsOpen = false;
            }
        }
    }
}
