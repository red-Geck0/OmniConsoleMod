using System;
using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>可映射的 XInput 輸入位識別碼（共 16 個：A/B/X/Y、LB/RB、LT/RT、LS/RS、DPad 4 向、LStick/RStick）。</summary>
    public enum GamepadInputId
    {
        A, B, X, Y,
        LB, RB,
        LT, RT,
        LS, RS,
        DPadUp, DPadDown, DPadLeft, DPadRight,
        LStick, RStick
    }

    /// <summary>動作型別（與 C++ ActionKind 一一對應）。</summary>
    public enum GamepadActionKind
    {
        None,
        KeyTap,
        KeyHold,
        KeyCombo,
        MouseButton,
        MouseWheel,
        StickCursor,
        StickScroll,
        StickArrows,
        StickWasd,
        TouchKeyboard
    }

    /// <summary>滑鼠鍵位。</summary>
    public enum GamepadMouseWhich { Left, Right, Middle }

    /// <summary>滾輪方向。</summary>
    public enum GamepadWheelDir { Up, Down, Left, Right }

    /// <summary>螢幕鍵盤呼叫方式：Com=觸控鍵盤（TabTip COM）、Osk=傳統螢幕鍵盤（osk.exe）。</summary>
    public enum VkbMethod { Com, Osk }

    /// <summary>組合鍵的修飾鍵旗標集（可任意子集組合）。</summary>
    [Flags]
    public enum GamepadModifier
    {
        None = 0,
        Ctrl = 1 << 0,
        Shift = 1 << 1,
        Alt = 1 << 2,
        Win = 1 << 3
    }

    /// <summary>單一輸入位的動作映射。</summary>
    public sealed class GamepadAction
    {
        /// <summary>動作型別。</summary>
        public GamepadActionKind Kind { get; set; } = GamepadActionKind.None;

        /// <summary>KeyTap / KeyHold / KeyCombo 的主鍵 VK code。</summary>
        public int Vk { get; set; }

        /// <summary>KeyCombo 的修飾鍵旗標集。</summary>
        public GamepadModifier Mods { get; set; } = GamepadModifier.None;

        /// <summary>MouseButton 的鍵位。</summary>
        public GamepadMouseWhich Which { get; set; } = GamepadMouseWhich.Left;

        /// <summary>MouseWheel 的方向。</summary>
        public GamepadWheelDir Dir { get; set; } = GamepadWheelDir.Up;

        /// <summary>TouchKeyboard 的呼叫方式。</summary>
        public VkbMethod Vkb { get; set; } = VkbMethod.Com;

        /// <summary>產生獨立的深拷貝。</summary>
        public GamepadAction Clone()
        {
            return new GamepadAction
            {
                Kind = Kind,
                Vk = Vk,
                Mods = Mods,
                Which = Which,
                Dir = Dir,
                Vkb = Vkb
            };
        }
    }

    /// <summary>Layered Mode 的啟用方式：HoldRelease=按住才生效、放開即失效；DoubleTapToggle=雙擊切換。</summary>
    public enum ProfileActivationMode { HoldRelease, DoubleTapToggle }

    /// <summary>一份 profile 的 Layered Mode 設定。</summary>
    public sealed class ProfileLayered
    {
        /// <summary>是否啟用 Layered Mode（啟用時映射僅在 layer 作用中才生效）。</summary>
        public bool Enabled { get; set; }

        /// <summary>觸發 layer 的輸入位；該鍵在 Layered 啟用時被保留作切換用，不送出原映射。</summary>
        public GamepadInputId TriggerKey { get; set; } = GamepadInputId.RS;

        /// <summary>啟用方式。</summary>
        public ProfileActivationMode ActivationMode { get; set; } = ProfileActivationMode.HoldRelease;

        /// <summary>產生獨立的深拷貝。</summary>
        public ProfileLayered Clone()
        {
            return new ProfileLayered
            {
                Enabled = Enabled,
                TriggerKey = TriggerKey,
                ActivationMode = ActivationMode
            };
        }
    }

    /// <summary>
    /// 一份手把映射 profile：可命名、可重用，由 App 透過 assignment 指派。
    /// 內建 profile（OmniNav / Classic / Gaming / None）以 IsBuiltIn 標記。
    /// </summary>
    public sealed class GamepadProfile
    {
        /// <summary>穩定識別碼：內建用 slug（omninav/classic/gaming/none），使用者建立的用 GUID。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>使用者可見名稱（內建 profile 亦可重新命名，OmniNav/Classic 除外）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>是否為內建 profile。</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>是否唯讀（OmniNav / Classic 唯讀；Gaming 與使用者 profile 可編輯）。</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>游標移動速度百分比（25–200，預設 100）。</summary>
        public int CursorSpeedPercent { get; set; } = 100;

        /// <summary>
        /// D-pad 是否依 OS 鍵盤重複設定補送 keydown（true=導覽用、false=遊戲用純鏡像按住）。
        /// 取代舊版 Tick / TickWithBindings 的差異。
        /// </summary>
        public bool DpadAutoRepeat { get; set; } = true;

        /// <summary>Layered Mode 設定。</summary>
        public ProfileLayered Layered { get; set; } = new ProfileLayered();

        /// <summary>16 個輸入位的映射；缺項視為 None。</summary>
        public Dictionary<GamepadInputId, GamepadAction> Bindings { get; set; } = new Dictionary<GamepadInputId, GamepadAction>();

        /// <summary>取得指定輸入位的動作；缺項回 Kind=None 的動作但不改變本物件狀態。</summary>
        public GamepadAction Get(GamepadInputId id)
        {
            if (Bindings.TryGetValue(id, out var a) && a != null) return a;
            return new GamepadAction { Kind = GamepadActionKind.None };
        }

        /// <summary>判定 bindings 是否「實際全為 None」（玩家清光時可提示）。</summary>
        public bool IsEffectivelyEmpty()
        {
            foreach (var kv in Bindings)
                if (kv.Value != null && kv.Value.Kind != GamepadActionKind.None) return false;
            return true;
        }

        /// <summary>產生獨立的深拷貝。</summary>
        public GamepadProfile Clone()
        {
            var clone = new GamepadProfile
            {
                Id = Id,
                Name = Name,
                IsBuiltIn = IsBuiltIn,
                IsReadOnly = IsReadOnly,
                CursorSpeedPercent = CursorSpeedPercent,
                DpadAutoRepeat = DpadAutoRepeat,
                Layered = Layered?.Clone() ?? new ProfileLayered(),
                Bindings = new Dictionary<GamepadInputId, GamepadAction>(Bindings.Count)
            };
            foreach (var kv in Bindings)
                clone.Bindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
            return clone;
        }
    }

    /// <summary>內建版面與內建 profile 的定義來源（first-run 種子與 reset-to-default 皆取自此）。</summary>
    public static class GamepadBuiltInLayouts
    {
        /// <summary>內建 profile 的固定識別碼。</summary>
        public const string OmniNavId = "omninav";
        public const string ClassicId = "classic";
        public const string GamingId = "gaming";
        public const string NoneId = "none";

        // VK 常數
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_TAB = 0x09;
        private const int VK_NEXT = 0x22;  // PageDown
        private const int VK_PRIOR = 0x21;  // PageUp
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;

        /// <summary>內建 OmniNav 配置（與 C++ MakeOmniNav() 逐鍵相同）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> OmniNav()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_NEXT },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_PRIOR },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Ctrl | GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Ctrl, Vk = VK_TAB },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_ESCAPE },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN },
                [GamepadInputId.LS] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_UP },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_DOWN },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_LEFT },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RIGHT },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
            };
        }

        /// <summary>內建 Classic 配置（與 C++ MakeClassic() 逐鍵相同）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> Classic()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_ESCAPE },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_NEXT },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_PRIOR },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.LS] = new GamepadAction { Kind = GamepadActionKind.None },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.None },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_UP },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_DOWN },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_LEFT },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RIGHT },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
            };
        }

        /// <summary>
        /// 產生四份內建 profile（OmniNav / Classic / Gaming / None）。
        /// first-run 種子與 reset-to-default 皆取自此；每次呼叫產生全新物件。
        /// None profile 有空映射，指派給 app 等同停用 mouse mode（黑名單效果）。
        /// </summary>
        public static List<GamepadProfile> BuiltInProfiles()
        {
            return new List<GamepadProfile>
            {
                new GamepadProfile
                {
                    Id = OmniNavId,
                    Name = "OmniNav",
                    IsBuiltIn = true,
                    IsReadOnly = true,
                    CursorSpeedPercent = 100,
                    DpadAutoRepeat = true,
                    Layered = new ProfileLayered { Enabled = false },
                    Bindings = OmniNav()
                },
                new GamepadProfile
                {
                    Id = ClassicId,
                    Name = "Classic",
                    IsBuiltIn = true,
                    IsReadOnly = true,
                    CursorSpeedPercent = 100,
                    DpadAutoRepeat = true,
                    Layered = new ProfileLayered { Enabled = false },
                    Bindings = Classic()
                },
                new GamepadProfile
                {
                    Id = GamingId,
                    Name = "Gaming",
                    IsBuiltIn = true,
                    IsReadOnly = false,
                    CursorSpeedPercent = 100,
                    DpadAutoRepeat = true,
                    Layered = new ProfileLayered
                    {
                        Enabled = true,
                        TriggerKey = GamepadInputId.RS,
                        ActivationMode = ProfileActivationMode.HoldRelease
                    },
                    Bindings = OmniNav()
                },
                new GamepadProfile
                {
                    Id = NoneId,
                    Name = "None",
                    IsBuiltIn = true,
                    IsReadOnly = true,
                    CursorSpeedPercent = 100,
                    DpadAutoRepeat = false,
                    Layered = new ProfileLayered { Enabled = false },
                    Bindings = new Dictionary<GamepadInputId, GamepadAction>()
                },
            };
        }
    }
}
