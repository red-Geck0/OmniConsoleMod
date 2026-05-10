using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OmniConsole.Services
{
    /// <summary>
    /// Konversi antara raw mapping string (yang disimpan di INI / SettingsService)
    /// dan tampilan UI manusiawi.
    ///
    /// Format raw: "ctrl+shift+tab", "lclick", "wheelup", "f5", "" (none).
    /// Format display: "Ctrl + Shift + Tab", "Left Click", "Wheel Up", "F5", "—".
    /// </summary>
    public static class MappingFormatter
    {
        /// <summary>Daftar token modifier yang valid (lowercase).</summary>
        public static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            "ctrl", "shift", "alt"
        };

        /// <summary>Token aksi non-keyboard (lowercase).</summary>
        public static readonly Dictionary<string, string> SpecialActionDisplay = new(StringComparer.OrdinalIgnoreCase)
        {
            ["lclick"]     = "Left Click",
            ["rclick"]     = "Right Click",
            ["mclick"]     = "Middle Click",
            ["wheelup"]    = "Wheel Up",
            ["wheeldown"]  = "Wheel Down",
            ["wheelleft"]  = "Wheel Left",
            ["wheelright"] = "Wheel Right",
            // Virtual keyboard — dua metode berbeda untuk kompatibilitas Windows 11
            ["vkb_com"]    = "Touch Keyboard (Method 1 – COM)",
            ["vkb_osk"]    = "On-Screen Keyboard (Method 2 – osk.exe)",
        };

        /// <summary>Daftar key (selain modifier & special) yang dapat dipilih user.</summary>
        public static readonly string[] AllSelectableKeys =
        [
            "Tab", "Escape", "Enter", "Space", "Backspace", "Delete", "Insert",
            "Home", "End", "Page Up", "Page Down",
            "Up", "Down", "Left", "Right",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        ];

        /// <summary>Daftar special action yang dapat dipilih user (display label).</summary>
        public static readonly string[] AllSpecialActions =
        [
            "Left Click", "Right Click", "Middle Click",
            "Wheel Up", "Wheel Down", "Wheel Left", "Wheel Right",
            "Touch Keyboard (Method 1 – COM)",
            "On-Screen Keyboard (Method 2 – osk.exe)",
        ];

        /// <summary>
        /// Convert raw → display. "" → "—", "lclick" → "Left Click",
        /// "ctrl+shift+tab" → "Ctrl + Shift + Tab".
        /// </summary>
        public static string ToDisplay(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "—";

            var tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim().ToLowerInvariant())
                            .Where(t => t.Length > 0)
                            .ToList();
            if (tokens.Count == 0) return "—";

            // Special action (single token only)
            if (tokens.Count == 1 && SpecialActionDisplay.TryGetValue(tokens[0], out var disp))
                return disp;

            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(" + ");
                sb.Append(FormatToken(tokens[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parse raw mapping menjadi tuple (modifiers, mainKeyDisplay, specialAction).
        /// specialAction != null jika raw adalah special (lclick/wheel/dll).
        /// </summary>
        public static (bool ctrl, bool shift, bool alt, string? key, string? specialAction) Parse(string raw)
        {
            bool ctrl = false, shift = false, alt = false;
            string? key = null, special = null;

            if (string.IsNullOrWhiteSpace(raw)) return (ctrl, shift, alt, null, null);

            var tokens = raw.Split('+', StringSplitOptions.RemoveEmptyEntries)
                            .Select(t => t.Trim().ToLowerInvariant())
                            .Where(t => t.Length > 0)
                            .ToList();
            if (tokens.Count == 0) return (ctrl, shift, alt, null, null);

            // Special action (single token only)
            if (tokens.Count == 1 && SpecialActionDisplay.TryGetValue(tokens[0], out var disp))
            {
                special = disp;
                return (ctrl, shift, alt, null, special);
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                bool isLast = (i == tokens.Count - 1);
                var t = tokens[i];
                if (!isLast)
                {
                    if (t == "ctrl" || t == "control") ctrl = true;
                    else if (t == "shift") shift = true;
                    else if (t == "alt") alt = true;
                }
                else
                {
                    key = FormatToken(t);
                }
            }
            return (ctrl, shift, alt, key, null);
        }

        /// <summary>
        /// Build raw mapping dari pilihan user di dialog.
        /// Special action override modifier+key.
        /// </summary>
        public static string Build(bool ctrl, bool shift, bool alt, string? keyDisplay, string? specialActionDisplay)
        {
            // Special action: single token
            if (!string.IsNullOrEmpty(specialActionDisplay))
            {
                foreach (var kv in SpecialActionDisplay)
                    if (kv.Value == specialActionDisplay) return kv.Key;
                return "";
            }
            if (string.IsNullOrEmpty(keyDisplay)) return "";

            var parts = new List<string>();
            if (ctrl) parts.Add("ctrl");
            if (shift) parts.Add("shift");
            if (alt) parts.Add("alt");
            parts.Add(DisplayToToken(keyDisplay));
            return string.Join("+", parts);
        }

        /// <summary>"tab" → "Tab", "pgdn" → "Page Down", "f5" → "F5", "a" → "A".</summary>
        private static string FormatToken(string lowerToken)
        {
            switch (lowerToken)
            {
                case "ctrl": case "control": return "Ctrl";
                case "shift": return "Shift";
                case "alt":   return "Alt";
                case "tab":   return "Tab";
                case "escape": case "esc": return "Escape";
                case "enter": case "return": return "Enter";
                case "space": return "Space";
                case "backspace": return "Backspace";
                case "delete": case "del": return "Delete";
                case "insert": return "Insert";
                case "home": return "Home";
                case "end":  return "End";
                case "pageup":   case "pgup": return "Page Up";
                case "pagedown": case "pgdn": return "Page Down";
                case "up":    return "Up";
                case "down":  return "Down";
                case "left":  return "Left";
                case "right": return "Right";
            }
            // F1..F12
            if (lowerToken.Length >= 2 && lowerToken[0] == 'f' &&
                int.TryParse(lowerToken.AsSpan(1), out int n) && n >= 1 && n <= 12)
                return "F" + n.ToString();
            // A..Z
            if (lowerToken.Length == 1 && lowerToken[0] >= 'a' && lowerToken[0] <= 'z')
                return ((char)(lowerToken[0] - 'a' + 'A')).ToString();
            // 0..9
            if (lowerToken.Length == 1 && lowerToken[0] >= '0' && lowerToken[0] <= '9')
                return lowerToken;
            return lowerToken;
        }

        /// <summary>Reverse: "Page Down" → "pgdn", "Tab" → "tab", "F5" → "f5".</summary>
        private static string DisplayToToken(string display)
        {
            switch (display)
            {
                case "Page Up":   return "pgup";
                case "Page Down": return "pgdn";
            }
            return display.Replace(" ", "").ToLowerInvariant();
        }
    }
}
