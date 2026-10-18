using System;
using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>可映射的 XInput 輸入位識別碼（共 16 個：A/B/X/Y、LB/RB、LT/RT、LS/RS、DPad 4 向、LStick/RStick）。
    /// 註：Start / Back 曾試著加入，但實測在 FSE 下收不到，故不列入。</summary>
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

        /// <summary>第 1 層映射（Layered 關閉時即唯一的一套）；缺項視為 None。</summary>
        public Dictionary<GamepadInputId, GamepadAction> Bindings { get; set; } = new Dictionary<GamepadInputId, GamepadAction>();

        /// <summary>
        /// 第 2 層映射，僅 <see cref="ProfileLayered.Enabled"/> 為 true 時使用
        /// （triggerKey 生效期間改套用這一套）。Layered 關閉時保留內容但不生效。
        /// </summary>
        public Dictionary<GamepadInputId, GamepadAction> LayerBindings { get; set; } = new Dictionary<GamepadInputId, GamepadAction>();

        /// <summary>取得指定層的映射表。</summary>
        public Dictionary<GamepadInputId, GamepadAction> BindingsOf(int layer) =>
            layer == 2 ? LayerBindings : Bindings;

        /// <summary>取得指定輸入位的動作；缺項回 Kind=None 的動作但不改變本物件狀態。</summary>
        public GamepadAction Get(GamepadInputId id) => Get(id, 1);

        /// <summary>取得指定層、指定輸入位的動作；缺項回 Kind=None 的動作但不改變本物件狀態。</summary>
        public GamepadAction Get(GamepadInputId id, int layer)
        {
            if (BindingsOf(layer).TryGetValue(id, out var a) && a != null) return a;
            return new GamepadAction { Kind = GamepadActionKind.None };
        }

        /// <summary>
        /// 判定此 profile 是否「實際全為 None」（玩家清光時可提示）。
        /// Layered 啟用時兩層都空才算空——第 1 層空、第 2 層有內容是完全正常的用法
        /// （平時不攔截，按住 trigger 才生效），不該被當成空 profile 停用 Mouse Mode。
        /// </summary>
        public bool IsEffectivelyEmpty()
        {
            if (!IsLayerEmpty(Bindings)) return false;
            if (Layered != null && Layered.Enabled && !IsLayerEmpty(LayerBindings)) return false;
            return true;
        }

        private static bool IsLayerEmpty(Dictionary<GamepadInputId, GamepadAction> bindings)
        {
            foreach (var kv in bindings)
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
                Bindings = new Dictionary<GamepadInputId, GamepadAction>(Bindings.Count),
                LayerBindings = new Dictionary<GamepadInputId, GamepadAction>(LayerBindings.Count)
            };
            foreach (var kv in Bindings)
                clone.Bindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
            foreach (var kv in LayerBindings)
                clone.LayerBindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
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
        // Apps / KeyboardOnly：非內建，僅於 first-run 種子（見 GamepadProfileStore.EnsureInitialized），
        // 之後可被使用者永久刪除（不像內建會自我復原）。沿用既有 GUID 以維持與舊 store 相容。
        public const string AppsId = "1bd3b4078abb4b77b50ce5d9db3e6b20";
        public const string KeyboardOnlyId = "4b9b05e9c8e64b73b43ad8378bf54d45";

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
        private const int VK_BACK = 0x08;   // Backspace
        private const int VK_HOME = 0x24;
        private const int VK_END = 0x23;
        private const int VK_INSERT = 0x2D;
        private const int VK_C = 0x43;
        private const int VK_R = 0x52;

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

        /// <summary>內建 Gaming 配置。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> Gaming()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_BACK },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.TouchKeyboard, Vkb = VkbMethod.Com },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_INSERT, Mods = GamepadModifier.None },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_C, Mods = GamepadModifier.Alt },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_R, Mods = GamepadModifier.Alt },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_R, Mods = GamepadModifier.Shift | GamepadModifier.Alt },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_PRIOR },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_NEXT },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_HOME },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_END },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
            };
        }

        /// <summary>內建 Apps 配置（與 OmniNav 相近，X=Backspace、Y=螢幕鍵盤）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> Apps()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_BACK },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.TouchKeyboard, Vkb = VkbMethod.Com },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_TAB, Mods = GamepadModifier.Ctrl | GamepadModifier.Shift },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_TAB, Mods = GamepadModifier.Ctrl },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_ESCAPE },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN },
                [GamepadInputId.LS] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_TAB, Mods = GamepadModifier.Shift },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_UP },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_DOWN },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_LEFT },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RIGHT },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
            };
        }

        /// <summary>內建 KeyboardOnly 配置（只有 Y=螢幕鍵盤，其餘皆 None）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> KeyboardOnly()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.TouchKeyboard, Vkb = VkbMethod.Com },
            };
        }

        /// <summary>
        /// 產生內建 profile（OmniNav / Classic / Gaming / None / Apps / KeyboardOnly）。
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
                    CursorSpeedPercent = 75,
                    DpadAutoRepeat = true,
                    Layered = new ProfileLayered
                    {
                        Enabled = true,
                        TriggerKey = GamepadInputId.RS,
                        ActivationMode = ProfileActivationMode.HoldRelease
                    },
                    // 第 1 層刻意留空：遊戲中平時不該攔截任何按鍵，按住 trigger
                    // 才切到第 2 層那套導覽用映射。
                    Bindings = new Dictionary<GamepadInputId, GamepadAction>(),
                    LayerBindings = Gaming()
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
