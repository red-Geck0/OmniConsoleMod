using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmniConsole.Dialogs;
using OmniConsole.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace OmniConsole.Controls
{
    /// <summary>x:Bind 用的清單列項目（profile 名稱 + 徽章 + 刪除鈕可見性）。</summary>
    public sealed class GamepadProfileRow
    {
        /// <summary>profile Id。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>顯示名稱。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>徽章文字（如「預設」）；空字串代表不顯示徽章。</summary>
        public string Badge { get; set; } = string.Empty;

        /// <summary>是否可刪除（內建 profile 為 false）。</summary>
        public bool CanDelete { get; set; }

        /// <summary>徽章是否顯示。</summary>
        public Visibility BadgeVisibility =>
            string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>刪除鈕是否顯示。</summary>
        public Visibility DeleteVisibility =>
            CanDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>手把映射「清單頁」UserControl：列出所有 profile，提供編輯/刪除入口。</summary>
    public sealed partial class GamepadProfileListView : UserControl
    {
        private readonly ResourceLoader _resw = ResourceLoader.GetForViewIndependentUse();
        private readonly ObservableCollection<GamepadProfileRow> _items = new ObservableCollection<GamepadProfileRow>();

        /// <summary>使用者要編輯某 profile（A 鍵或滑鼠點列項）時觸發，帶 profile Id。</summary>
        public event EventHandler<string>? EditRequested;

        /// <summary>子對話方塊開啟前 true、關閉後 false（宿主據此 Stop/StartGamepadPolling）。</summary>
        public event EventHandler<bool>? DialogActiveChanged;

        /// <summary>綁定 ListView ItemsSource 為內部 ObservableCollection，並掛 ListViewItem 焦點事件以同步 SelectedItem。</summary>
        public GamepadProfileListView()
        {
            InitializeComponent();
            ProfileList.ItemsSource = _items;
            ProfileList.ContainerContentChanging += ProfileList_ContainerContentChanging;
        }

        /// <summary>每次 ListViewItem 容器產生或重用時，掛上 GotFocus 同步 SelectedItem 到目前焦點 row。</summary>
        private void ProfileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is ListViewItem lvi)
            {
                lvi.GotFocus -= ListViewItem_GotFocus;
                lvi.GotFocus += ListViewItem_GotFocus;
            }
        }

        /// <summary>ListViewItem 拿到焦點時同步 SelectedItem，讓 selected background 與 selection indicator 跟隨焦點。</summary>
        private void ListViewItem_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem lvi && lvi.Content is GamepadProfileRow row)
                ProfileList.SelectedItem = row;
        }

        /// <summary>是否有任何 profile（新模型下永遠為 true，至少含三個內建 profile）。</summary>
        public bool HasItems => _items.Count > 0;

        /// <summary>從 GamepadProfileStore 重抓並重新整理清單。</summary>
        public void Refresh()
        {
            int prevIndex = ProfileList.SelectedIndex;
            _items.Clear();
            try
            {
                var data = GamepadProfileStore.Load();
                foreach (var p in data.Profiles)
                {
                    _items.Add(new GamepadProfileRow
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Badge = p.Id == data.DefaultProfileId ? Loc("GamepadProfileBadge_Default") : string.Empty,
                        CanDelete = !p.IsBuiltIn
                    });
                }
            }
            catch { }

            if (_items.Count > 0)
                ProfileList.SelectedIndex = (prevIndex >= 0 && prevIndex < _items.Count) ? prevIndex : 0;
        }

        /// <summary>將焦點程式化設給 ListView（清單頁進入時呼叫）。</summary>
        public void FocusList() => ProfileList.Focus(FocusState.Programmatic);

        /// <summary>取目前作用中的 row：先看焦點 ListViewItem（D-pad 移動只動焦點不更新 SelectedItem），回退到 SelectedItem。</summary>
        private GamepadProfileRow? GetActiveRow()
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is ListViewItem lvi)
            {
                if (lvi.Content is GamepadProfileRow focusedRow) return focusedRow;
                if (lvi.DataContext is GamepadProfileRow ctxRow) return ctxRow;
            }
            return ProfileList.SelectedItem as GamepadProfileRow;
        }

        /// <summary>目前焦點 profile 的 Id（宿主用於「設為預設」等操作）；無則回 null。</summary>
        public string? SelectedProfileId => GetActiveRow()?.Id;

        /// <summary>給宿主 A 鍵呼叫：發出 EditRequested 帶目前焦點 profile 的 Id。</summary>
        public void EditSelected()
        {
            var row = GetActiveRow();
            if (row != null && !string.IsNullOrEmpty(row.Id))
                EditRequested?.Invoke(this, row.Id);
        }

        /// <summary>給宿主 X 鍵呼叫：刪除目前焦點 profile（內建 profile 不可刪，忽略）。</summary>
        public Task DeleteSelectedAsync()
        {
            var row = GetActiveRow();
            if (row != null && row.CanDelete) return DeleteAsync(row.Id);
            return Task.CompletedTask;
        }

        /// <summary>滑鼠／手把 A 點某列：發出 EditRequested。</summary>
        private void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GamepadProfileRow row && !string.IsNullOrEmpty(row.Id))
                EditRequested?.Invoke(this, row.Id);
        }

        /// <summary>每列垃圾桶 Button 點擊：刪除該列對應 profile。</summary>
        private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string profileId)
                _ = DeleteAsync(profileId);
        }

        /// <summary>彈確認對話方塊，按下「是」才實際刪除並重新整理。期間透過 DialogActiveChanged 通知宿主 Stop/Start 手把輪詢。</summary>
        private async Task DeleteAsync(string profileId)
        {
            DialogActiveChanged?.Invoke(this, true);
            try
            {
                var dlg = new GamepadMessageDialog(
                    XamlRoot,
                    Loc("GamepadMappingDeleteConfirmTitle"),
                    Loc("GamepadMappingDeleteConfirmBody"),
                    Loc("GamepadMappingDeleteConfirmYes"),
                    Loc("GamepadMappingDeleteConfirmNo"));
                await dlg.ShowAsync();
                if (dlg.Result)
                {
                    GamepadProfileStore.DeleteProfile(profileId);
                    Refresh();
                }
            }
            finally
            {
                DialogActiveChanged?.Invoke(this, false);
                FocusList();
            }
        }

        /// <summary>resw 查詢；plain / .Text / .Content 三候選回退到 key 本身。</summary>
        private string Loc(string key)
        {
            string[] candidates = { key, key + "/Text", key + "/Content" };
            foreach (var c in candidates)
            {
                try
                {
                    var s = _resw.GetString(c);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            return key;
        }
    }
}
