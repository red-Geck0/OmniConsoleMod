using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// Dialog konfigurasi mapping satu tombol controller ke kombinasi keyboard
    /// atau aksi khusus (mouse click / wheel scroll).
    /// </summary>
    public sealed partial class ButtonMappingDialog : ContentDialog
    {
        private readonly ResourceLoader _resourceLoader = new();
        private bool _suppressEvents = true;

        /// <summary>Hasil mapping setelah user klik Save (raw INI format).</summary>
        public string? ResultMapping { get; private set; }

        /// <summary>
        /// Membuat dialog. Title menampilkan nama tombol + layout aktif.
        /// </summary>
        /// <param name="buttonId">ID tombol (mis. "A", "LB", "DPadUp").</param>
        /// <param name="layoutName">Nama layout ("OmniNav" atau "Classic").</param>
        /// <param name="initialMapping">Raw mapping awal untuk pre-populate UI.</param>
        public ButtonMappingDialog(string buttonId, string layoutName, string initialMapping)
        {
            InitializeComponent();

            string titleFmt = SafeGetString("MappingDialog_Title") ?? "Map Button";
            Title = $"{titleFmt} — {FormatButtonLabel(buttonId)} ({layoutName})";

            InstructionText.Text = SafeGetString("MappingDialog_Instruction") ?? "Pick a key combination or action below:";
            ModeLabel.Text       = SafeGetString("MappingDialog_Mode")        ?? "Action";
            ModifiersLabel.Text  = SafeGetString("MappingDialog_Modifiers")   ?? "Modifiers";
            KeyLabel.Text        = SafeGetString("MappingDialog_Key")         ?? "Key";
            SpecialLabel.Text    = SafeGetString("MappingDialog_Special")     ?? "Action";

            PrimaryButtonText = SafeGetString("PlatformDialog_Save")   ?? "Save";
            CloseButtonText   = SafeGetString("PlatformDialog_Cancel") ?? "Cancel";

            // Populate combobox lists
            foreach (var k in MappingFormatter.AllSelectableKeys)
                KeyCombo.Items.Add(new ComboBoxItem { Content = k });
            foreach (var s in MappingFormatter.AllSpecialActions)
                SpecialCombo.Items.Add(new ComboBoxItem { Content = s });

            // Pre-populate dari initialMapping
            var (ctrl, shift, alt, key, special) = MappingFormatter.Parse(initialMapping);
            CtrlCheck.IsChecked  = ctrl;
            ShiftCheck.IsChecked = shift;
            AltCheck.IsChecked   = alt;

            int modeIdx;
            if (special != null)
            {
                modeIdx = 2; // Special
                foreach (ComboBoxItem item in SpecialCombo.Items)
                    if ((string)item.Content == special) { SpecialCombo.SelectedItem = item; break; }
                if (SpecialCombo.SelectedIndex < 0) SpecialCombo.SelectedIndex = 0;
            }
            else if (key != null)
            {
                modeIdx = 1; // Keyboard
                foreach (ComboBoxItem item in KeyCombo.Items)
                    if ((string)item.Content == key) { KeyCombo.SelectedItem = item; break; }
                if (KeyCombo.SelectedIndex < 0) KeyCombo.SelectedIndex = 0;
            }
            else
            {
                modeIdx = 0; // None
                if (KeyCombo.SelectedIndex < 0) KeyCombo.SelectedIndex = 0;
                if (SpecialCombo.SelectedIndex < 0) SpecialCombo.SelectedIndex = 0;
            }
            ModeRadios.SelectedIndex = modeIdx;

            _suppressEvents = false;
            UpdateModeVisibility();
            UpdatePreview();

            PrimaryButtonClick += OnPrimaryClick;
        }

        private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            int mode = ModeRadios.SelectedIndex;
            if (mode <= 0)
            {
                ResultMapping = "";
                return;
            }
            if (mode == 1)
            {
                string? key = (KeyCombo.SelectedItem as ComboBoxItem)?.Content as string;
                ResultMapping = MappingFormatter.Build(
                    CtrlCheck.IsChecked == true,
                    ShiftCheck.IsChecked == true,
                    AltCheck.IsChecked == true,
                    key, null);
            }
            else
            {
                string? special = (SpecialCombo.SelectedItem as ComboBoxItem)?.Content as string;
                ResultMapping = MappingFormatter.Build(false, false, false, null, special);
            }
        }

        private void ModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            UpdateModeVisibility();
            UpdatePreview();
        }

        private void ModifierChanged(object sender, RoutedEventArgs e) => UpdatePreview();
        private void KeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
        private void SpecialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

        private void UpdateModeVisibility()
        {
            int mode = ModeRadios?.SelectedIndex ?? 0;
            // 0=None, 1=Keyboard, 2=Special
            if (KeyboardPanel != null) KeyboardPanel.Visibility = (mode == 1) ? Visibility.Visible : Visibility.Collapsed;
            if (SpecialPanel != null)  SpecialPanel.Visibility  = (mode == 2) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePreview()
        {
            if (PreviewText == null || ModeRadios == null) return;
            int mode = ModeRadios.SelectedIndex;
            if (mode <= 0) { PreviewText.Text = "—"; return; }

            if (mode == 1)
            {
                string? key = (KeyCombo?.SelectedItem as ComboBoxItem)?.Content as string;
                string raw = MappingFormatter.Build(
                    CtrlCheck?.IsChecked == true,
                    ShiftCheck?.IsChecked == true,
                    AltCheck?.IsChecked == true,
                    key, null);
                PreviewText.Text = MappingFormatter.ToDisplay(raw);
            }
            else
            {
                string? special = (SpecialCombo?.SelectedItem as ComboBoxItem)?.Content as string;
                string raw = MappingFormatter.Build(false, false, false, null, special);
                PreviewText.Text = MappingFormatter.ToDisplay(raw);
            }
        }

        private static string FormatButtonLabel(string id) => id switch
        {
            "LSPress" => "LS Press",
            "RSPress" => "RS Press",
            "DPadUp" => "D-Pad ↑",
            "DPadDown" => "D-Pad ↓",
            "DPadLeft" => "D-Pad ←",
            "DPadRight" => "D-Pad →",
            _ => id,
        };

        private string? SafeGetString(string key)
        {
            try { return _resourceLoader.GetString(key); }
            catch { return null; }
        }
    }
}
