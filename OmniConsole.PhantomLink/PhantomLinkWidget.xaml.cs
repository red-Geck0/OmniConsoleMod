using OmniConsole.PhantomLink.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace OmniConsole.PhantomLink
{
    /// <summary>
    /// PhantomLink Game Bar widget 主面板：前景程式 + profile 指派、Quick Actions、
    /// Steam In-Game Overlay、Mouse Mode On/Off。
    /// 設定值透過 PhantomKeyStore 寫入 Shared.ini，動作委派給 PhantomBridge COM server。
    /// </summary>
    public sealed partial class PhantomLinkWidget : Page
    {
        private bool _loading;
        private bool _builtInMapping;

        // 前景程式狀態：appId（"process:xxx" / "aumid:xxx"；null=取不到或黑名單）與其完整路徑
        private string _foregroundAppId;
        private string _foregroundFullPath;  // Win32 桌面 process 才有，packaged 為空字串

        // 焦點剛從外部進入 Widget → 吞掉緊接著的一顆 D-pad Down，避免雙跳
        private DateTime _swallowNextDownUntil;

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public PhantomLinkWidget()
        {
            DebugLogger.Log("[Widget] ctor enter");
            try { this.InitializeComponent(); DebugLogger.Log("[Widget] InitializeComponent OK"); }
            catch (Exception ex) { DebugLogger.Log("[Widget] InitializeComponent FAIL: " + ex); throw; }

            this.PreviewKeyDown += OnPreviewKeyDown;
            this.GettingFocus += OnGettingFocus;

            this.Loaded += (s, e) =>
            {
                DebugLogger.Log("[Widget] Loaded");
                try { ReloadFromStore(); DebugLogger.Log("[Widget] Reload OK"); }
                catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }

                SyncThemeFromGameBar();

                try { Application.Current.LeavingBackground += OnLeavingBackground; }
                catch (Exception ex) { DebugLogger.Log("[Widget] Hook LeavingBackground FAIL: " + ex); }

                var w = App.CurrentWidget;
                if (w != null)
                {
                    try { w.RequestedThemeChanged += OnGameBarThemeChanged; }
                    catch (Exception ex) { DebugLogger.Log("[Widget] Hook ThemeChanged FAIL: " + ex); }
                }
            };

            this.Unloaded += (s, e) =>
            {
                try { Application.Current.LeavingBackground -= OnLeavingBackground; } catch { }
                var w = App.CurrentWidget;
                if (w != null) { try { w.RequestedThemeChanged -= OnGameBarThemeChanged; } catch { } }
            };
        }

        // ── Game Bar 主題同步 ────────────────────────────────────────────────

        /// <summary>
        /// Page 預設不跟隨 XboxGameBarWidget.RequestedTheme，必須手動橋接
        /// 才能在 Game Bar Light/Dark 主題下正確顯示文字顏色。
        /// </summary>
        private void SyncThemeFromGameBar()
        {
            var w = App.CurrentWidget;
            if (w == null) return;
            this.RequestedTheme = w.RequestedTheme;
        }

        /// <summary>Game Bar 主題變更事件：marshal 回 UI 執行緒套用 SyncThemeFromGameBar。</summary>
        private async void OnGameBarThemeChanged(Microsoft.Gaming.XboxGameBar.XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, SyncThemeFromGameBar);
        }

        // ── 設定重新載入 ─────────────────────────────────────────────────────

        /// <summary>從背景返回前景時重新讀取設定，反映外部行程（OmniConsole 主程式）的變更。</summary>
        private void OnLeavingBackground(object sender, Windows.ApplicationModel.LeavingBackgroundEventArgs e)
        {
            DebugLogger.Log("[Widget] LeavingBackground → reload");
            try { ReloadFromStore(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }
        }

        // ── 焦點進入偵測：重導至選中態按鈕 + 吞掉進入時的 D-pad Down ─────────

        /// <summary>
        /// 焦點從 Widget 外部進入 → 重導到第一 section 的落點控制項，並開啟 150ms 吞 Down 窗。
        /// </summary>
        private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var oldFE = args.OldFocusedElement as DependencyObject;
            if (!IsDescendant(oldFE))
            {
                _swallowNextDownUntil = DateTime.UtcNow.AddMilliseconds(150);
                var target = PickFocusTarget(ForegroundAppSection);
                if (target != null && !ReferenceEquals(target, args.NewFocusedElement))
                {
                    try { args.TrySetNewFocusedElement(target); }
                    catch (Exception ex) { DebugLogger.Log("[Widget] TrySetNewFocusedElement FAIL: " + ex); }
                }
                DebugLogger.Log("[Widget] focus re-entered → redirect + arm swallow-Down");
            }
        }

        /// <summary>判斷節點是否為本 Page 視覺樹內的子元素。</summary>
        private bool IsDescendant(DependencyObject node)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, this)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ── 跨 Section D-pad 導航 ───────────────────────────────────────────

        /// <summary>跨 section D-pad 導航：落點挑選中態的控制項。</summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool down = e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.Down;
            bool up = e.Key == VirtualKey.GamepadDPadUp || e.Key == VirtualKey.Up;
            if (!down && !up) return;

            if (down && DateTime.UtcNow < _swallowNextDownUntil)
            {
                _swallowNextDownUntil = DateTime.MinValue;
                DebugLogger.Log("[Widget] swallow entry Down");
                e.Handled = true;
                return;
            }

            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;

            var currentSection = FindSection(focused);
            if (currentSection == null) return;

            var sections = RootPanel.Children.OfType<FrameworkElement>().ToList();
            int idx = sections.IndexOf(currentSection);
            int step = down ? 1 : -1;
            for (int i = idx + step; i >= 0 && i < sections.Count; i += step)
            {
                if (FocusSection(sections[i])) { e.Handled = true; return; }
            }
        }

        /// <summary>走到 RootPanel 的直屬子元素，作為「section」代表。</summary>
        private FrameworkElement FindSection(DependencyObject node)
        {
            while (node != null)
            {
                var parent = VisualTreeHelper.GetParent(node);
                if (parent == RootPanel) return node as FrameworkElement;
                node = parent;
            }
            return null;
        }

        /// <summary>
        /// Section 內挑焦點目標：checked ToggleButton &gt; 第一顆 ToggleButton &gt; ToggleSwitch
        /// &gt; Slider &gt; ComboBox &gt; Button。
        /// </summary>
        private Control PickFocusTarget(FrameworkElement section)
        {
            if (section == null) return null;
            var toggles = FindDescendants<ToggleButton>(section).Where(t => t.IsEnabled).ToList();
            if (toggles.Count > 0)
                return toggles.FirstOrDefault(t => t.IsChecked == true) ?? toggles[0];
            var toggleSwitch = FindDescendants<ToggleSwitch>(section).FirstOrDefault(s => s.IsEnabled);
            if (toggleSwitch != null) return toggleSwitch;
            var slider = FindDescendants<Slider>(section).FirstOrDefault(s => s.IsEnabled);
            if (slider != null) return slider;
            var combo = FindDescendants<ComboBox>(section).FirstOrDefault(c => c.IsEnabled);
            if (combo != null) return combo;
            return FindDescendants<Button>(section).FirstOrDefault(b => b.IsEnabled);
        }

        /// <summary>聚焦 section 的落點控制項；供跨 section 導航呼叫。</summary>
        private bool FocusSection(FrameworkElement section)
        {
            var target = PickFocusTarget(section);
            return target != null && target.Focus(FocusState.Keyboard);
        }

        /// <summary>遞迴走訪視覺樹，列舉所有指定型別的子元素。</summary>
        private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in FindDescendants<T>(child)) yield return d;
            }
        }

        // ── 資料綁定與啟用狀態 ──────────────────────────────────────────────

        /// <summary>
        /// 從 Shared.ini 讀值並同步所有 UI 控制項狀態。
        /// _loading 旗標避免同步過程觸發 Toggled/SelectionChanged 回寫造成遞迴。
        /// </summary>
        private void ReloadFromStore()
        {
            _loading = true;
            try
            {
                PhantomKeyStore.EnsureDefaultsIfMissing();
                _builtInMapping = HardwareDetection.HasBuiltInGamepadMapping();

                // SteamInGameOverlay 觸發按鈕條件可見性（FSE 模式 + DefaultPlatform=SteamBigPicture）
                string defaultPlatform = PhantomKeyStore.GetDefaultPlatform();
                bool steamBtnVisible =
                    FseStatus.IsActive() &&
                    defaultPlatform == PhantomKeyStore.PlatformSteamBigPicture;
                TriggerSteamInGameOverlayBtn.Visibility =
                    steamBtnVisible ? Visibility.Visible : Visibility.Collapsed;
                SteamInGameOverlayOffBtn.XYFocusRight =
                    steamBtnVisible ? (DependencyObject)TriggerSteamInGameOverlayBtn : SteamInGameOverlayOnBtn;
                SteamInGameOverlayOnBtn.XYFocusLeft =
                    steamBtnVisible ? (DependencyObject)TriggerSteamInGameOverlayBtn : SteamInGameOverlayOffBtn;
                SteamInGameOverlayButtonRow.Spacing = steamBtnVisible ? 6 : 3;

                // Steam In-Game Overlay（獨立於 Mouse Mode）
                bool overlay = PhantomKeyStore.GetSteamInGameOverlayEnabled();
                SteamInGameOverlayOnBtn.IsChecked = overlay;
                SteamInGameOverlayOffBtn.IsChecked = !overlay;

                // Mouse Mode On/Off
                ModeSwitch.IsOn = PhantomKeyStore.GetMouseModeEnabled();

                // profile 下拉清單
                PopulateProfileCombo();

                ApplyEnabledState();

                // 前景程式區塊（每次 reload 同步重抓一次）
                RefreshForegroundApp();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>從 Shared.ini [Profiles] 區段填入 profile 下拉清單（Tag=profile id）。</summary>
        private void PopulateProfileCombo()
        {
            ProfileCombo.Items.Clear();
            try
            {
                foreach (var (id, name) in PhantomKeyStore.GetProfileList())
                    ProfileCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] PopulateProfileCombo FAIL: " + ex);
            }
            UpdateEditProfileEnabled();
        }

        /// <summary>
        /// 呼叫 PhantomBridge.GetForegroundAppInfo 取前景資訊，更新 ForegroundAppLineText
        /// 與 _foregroundAppId / _foregroundFullPath；依黑名單、內建廠商映射、elevated 狀態
        /// 決定 ProfileCombo 是否可用於指派與 CustomizeAppNoteText 的可見性。
        /// </summary>
        private void RefreshForegroundApp()
        {
            var resw = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();
            string title = string.Empty;
            string proc = string.Empty;
            string fullPath = string.Empty;
            string aumid = string.Empty;
            string displayName = string.Empty;
            bool isElevated = false;
            try
            {
                var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.GetForegroundAppInfo(out title, out proc, out fullPath, out aumid, out displayName, out isElevated);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] GetForegroundAppInfo failed: " + ex.Message);
                ForegroundAppLineText.Text = LocSafe(resw, "Widget_ForegroundApp_None", "Current: —");
                _foregroundAppId = null;
                _foregroundFullPath = string.Empty;
                CustomizeAppNoteText.Visibility = Visibility.Collapsed;
                UpdateEditProfileEnabled();
                return;
            }

            string identifier = !string.IsNullOrEmpty(displayName) ? displayName : (!string.IsNullOrEmpty(proc) ? proc : "—");
            string desc = proc ?? string.Empty;

            string lineText;
            if (string.IsNullOrEmpty(identifier) || identifier == "—")
            {
                lineText = LocSafe(resw, "Widget_ForegroundApp_None", "Current: —");
            }
            else if (string.IsNullOrEmpty(desc) ||
                     string.Equals(desc, identifier, StringComparison.OrdinalIgnoreCase))
            {
                string fmt = LocSafe(resw, "Widget_ForegroundApp_LineFormat_NoDesc", "Current: {0}");
                lineText = string.Format(fmt, identifier);
            }
            else
            {
                string fmt = LocSafe(resw, "Widget_ForegroundApp_LineFormat", "Current: {0} ({1})");
                lineText = string.Format(fmt, identifier, desc);
            }
            ForegroundAppLineText.Text = lineText;

            bool isUwp = !string.IsNullOrEmpty(aumid);

            // 黑名單比對：process 名單命中或 AUMID 內含任一 PFN 子字串即擋
            bool blocked = false;
            if (!string.IsNullOrEmpty(proc))
                blocked = IsBlacklistedProcess(proc);
            if (!blocked && isUwp)
            {
                blocked = aumid.IndexOf("Microsoft.GamingApp", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("B9ECED6F.ArmouryCrateSE", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("windows.immersivecontrolpanel", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("b5fbce6b-2d7d-4da0-b419-4beb30e2b808", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // packaged 優先用 aumid: 前綴，桌面 process 用 process: 前綴
            if (blocked)
                _foregroundAppId = null;
            else if (isUwp)
                _foregroundAppId = "aumid:" + aumid;
            else if (!string.IsNullOrEmpty(proc))
                _foregroundAppId = "process:" + proc;
            else
                _foregroundAppId = null;
            // packaged 行程或 blocked 時 fullPath 不適用
            _foregroundFullPath = (!blocked && !isUwp) ? (fullPath ?? string.Empty) : string.Empty;

            CustomizeAppNoteText.Visibility =
                (isElevated && _foregroundAppId != null) ? Visibility.Visible : Visibility.Collapsed;

            UpdateEditProfileEnabled();
        }

        /// <summary>是否可把目前前景 App 指派到 profile（有有效 appId、無內建廠商映射）。</summary>
        private bool CanAssignForeground => _foregroundAppId != null && !_builtInMapping;

        /// <summary>EditProfileBtn 啟用條件：下拉清單已選一個 profile。</summary>
        private void UpdateEditProfileEnabled()
        {
            EditProfileBtn.IsEnabled = ProfileCombo.SelectedItem is ComboBoxItem;
        }

        /// <summary>resw 安全查詢：不存在或擲例外時回退到 `fallback` 參數值。</summary>
        private static string LocSafe(Windows.ApplicationModel.Resources.ResourceLoader resw, string key, string fallback)
        {
            try
            {
                var s = resw.GetString(key);
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch { return fallback; }
        }

        /// <summary>行程名稱比對（大小寫不敏感）是否為系統黑名單 Tier-1 程式。</summary>
        private static bool IsBlacklistedProcess(string proc)
        {
            string[] names =
            {
                "OmniConsole", "Playnite.FullscreenApp", "steamwebhelper",
            };
            foreach (var n in names)
                if (string.Equals(proc, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 套用 IsEnabled 規則：內建廠商手把映射存在（ROG Ally 等）→ Mouse Mode 開關停用、顯示說明。
        /// </summary>
        private void ApplyEnabledState()
        {
            ModeSwitch.IsEnabled = !_builtInMapping;
            BuiltInMappingNote.Visibility = _builtInMapping ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Quick Actions：一次性動作按鈕（委派給 PhantomBridge COM server） ─────

        /// <summary>透過 PhantomBridge 送 Win+Tab 開啟 Windows 工作檢視。</summary>
        private void TaskViewBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] TaskViewBtn_Click → PhantomBridge.SendTaskView");
            try { PhantomBridgeHelper.CreateFactory().SendTaskView(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] TaskView FAIL: " + ex); }
        }

        /// <summary>透過 PhantomBridge 觸發 Steam In-Game Overlay。</summary>
        private void TriggerSteamInGameOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            string shortcut = PhantomKeyStore.GetSteamInGameOverlayShortcut();
            DebugLogger.Log($"[Widget] TriggerSteamInGameOverlayBtn_Click → PhantomBridge.TriggerSteamInGameOverlay(\"{shortcut}\")");
            try { PhantomBridgeHelper.CreateFactory().TriggerSteamInGameOverlay(shortcut); }
            catch (Exception ex) { DebugLogger.Log("[Widget] TriggerSteamInGameOverlay FAIL: " + ex); }
        }

        /// <summary>透過 PhantomBridge 啟動 xbox://library（Xbox 媒體櫃）。</summary>
        private void XboxLibraryBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] XboxLibraryBtn_Click → PhantomBridge.OpenXboxLibrary");
            try { PhantomBridgeHelper.CreateFactory().OpenXboxLibrary(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] OpenXboxLibrary FAIL: " + ex); }
        }

        // ── UI 事件處理 ─────────────────────────────────────────────────────

        /// <summary>
        /// Steam In-Game Overlay 兩顆 ToggleButton 共用 Click：On/Off 互斥切換、寫入 Store。
        /// </summary>
        private void SteamInGameOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton btn)) return;

            bool enabled = (btn.Tag as string) == "On";

            _loading = true;
            try
            {
                SteamInGameOverlayOnBtn.IsChecked = enabled;
                SteamInGameOverlayOffBtn.IsChecked = !enabled;
            }
            finally { _loading = false; }

            PhantomKeyStore.SetSteamInGameOverlayEnabled(enabled);
        }

        /// <summary>Mouse Mode On/Off 開關：寫入 Store。</summary>
        private void ModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            PhantomKeyStore.SetMouseModeEnabled(ModeSwitch.IsOn);
        }

        /// <summary>
        /// profile 下拉選擇變更：把目前前景 App 指派到所選 profile（委派 PhantomBridge COM）。
        /// 前景無有效 appId（黑名單 / 內建廠商映射）時僅更新編輯鈕狀態，不指派。
        /// </summary>
        private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateEditProfileEnabled();
            if (_loading) return;
            if (!(ProfileCombo.SelectedItem is ComboBoxItem item) || !(item.Tag is string profileId)) return;
            if (!CanAssignForeground) return;

            DebugLogger.Log($"[Widget] assign foreground [{_foregroundAppId}] → profile [{profileId}]");
            try
            {
                PhantomBridgeHelper.CreateFactory().SetProfileAssignment(
                    _foregroundAppId, profileId, _foregroundFullPath ?? string.Empty);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] SetProfileAssignment FAIL: " + ex.Message);
            }
        }

        /// <summary>
        /// 編輯 profile 按鈕：對下拉清單目前所選 profile 開啟主程式手把映射編輯器
        /// （委派 PhantomBridge COM → omniconsole://edit-gamepad-profile?profileId=...）。
        /// </summary>
        private void EditProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProfileCombo.SelectedItem is ComboBoxItem item) || !(item.Tag is string profileId)) return;
            DebugLogger.Log("[Widget] EditProfileBtn_Click → PhantomBridge.OpenProfileEditor: " + profileId);
            try { PhantomBridgeHelper.CreateFactory().OpenProfileEditor(profileId); }
            catch (Exception ex) { DebugLogger.Log("[Widget] OpenProfileEditor FAIL: " + ex.Message); }
        }
    }
}
