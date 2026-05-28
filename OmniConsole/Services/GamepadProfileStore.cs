using OmniConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>App → profile 的指派紀錄。</summary>
    public sealed class ProfileAssignment
    {
        /// <summary>被指派的 App 識別。</summary>
        public AppId AppId { get; set; } = new AppId();

        /// <summary>指派到的 profile Id。</summary>
        public string ProfileId { get; set; } = string.Empty;
    }

    /// <summary>整個 profile store 的內容：預設 profile + profile 清單 + App 指派清單。</summary>
    public sealed class GamepadProfileData
    {
        /// <summary>mode=On 時未被指派的 App 一律套用此 profile。</summary>
        public string DefaultProfileId { get; set; } = GamepadBuiltInLayouts.OmniNavLId;

        /// <summary>所有 profile（內建排在前，使用者建立的接續其後）。</summary>
        public List<GamepadProfile> Profiles { get; set; } = new List<GamepadProfile>();

        /// <summary>App → profile 指派清單。</summary>
        public List<ProfileAssignment> Assignments { get; set; } = new List<ProfileAssignment>();
    }

    /// <summary>
    /// 手把映射 profile 的持久化。
    /// 檔案位置：PublisherCacheFolder\OmniConsoleShared\GamepadProfiles.json
    /// （與 Shared.ini 同目錄；PhantomKey C++ 端讀，OmniConsole C# 端讀寫）。
    /// </summary>
    public static class GamepadProfileStore
    {
        private const string SharedFolderName = "OmniConsoleShared";
        private const string ProfilesFileName = "GamepadProfiles.json";
        private const int SchemaVersion = 2;

        private static string? _cachedPath;

        // ── 路徑解析 ──────────────────────────────────────────────────────────

        /// <summary>取 PublisherCacheFolder 下的 profile 檔完整路徑；首次取得後快取，取不到時回空字串。</summary>
        private static string ProfilesPath
        {
            get
            {
                if (_cachedPath != null) return _cachedPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedPath = Path.Combine(folder.Path, ProfilesFileName);
                }
                catch
                {
                    _cachedPath = string.Empty;
                }
                return _cachedPath;
            }
        }

        // ── 硬性黑名單（Tier 1：Mouse Mode 完全不介入的系統程式） ──────────────

        /// <summary>
        /// 判定 appId 是否屬於不開放指派 profile 的系統程式集合。
        /// 瀏覽器／檔案總管等不在此 — 新模型下它們是一般 App，會套用預設或被指派的 profile。
        /// </summary>
        public static bool IsBlacklisted(AppId appId)
        {
            if (appId == null || string.IsNullOrEmpty(appId.Value)) return false;
            if (appId.Kind == IdKind.Process)
                return s_blacklistedProcesses.Contains(appId.Value);
            foreach (var sub in s_blacklistedPfnSubstrings)
                if (appId.Value.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>process 名比對集合（大小寫不敏感）。</summary>
        private static readonly HashSet<string> s_blacklistedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "OmniConsole",
            "Playnite.FullscreenApp",
        };

        /// <summary>AUMID 形如 &lt;PFN&gt;!&lt;AppId&gt;，對 AUMID 整段做子字串搜尋。</summary>
        private static readonly string[] s_blacklistedPfnSubstrings =
        {
            "Microsoft.GamingApp",                      // Xbox App
            "B9ECED6F.ArmouryCrateSE",                  // Armoury Crate SE
            "Microsoft.WindowsStore",                   // Microsoft Store（避免使用者誤指派造成 Store 內手把導航衝突）
            "b5fbce6b-2d7d-4da0-b419-4beb30e2b808",     // OmniConsole 主程式自己（packaged）
        };

        // ── 內建 profile id 判定 ──────────────────────────────────────────────

        /// <summary>判定 id 是否為內建 profile。</summary>
        public static bool IsBuiltInId(string? id) =>
            id == GamepadBuiltInLayouts.OmniNavId ||
            id == GamepadBuiltInLayouts.ClassicId ||
            id == GamepadBuiltInLayouts.OmniNavLId ||
            id == GamepadBuiltInLayouts.NoneId;

        // ── 讀 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 讀取整個 store。檔案不存在 / 解析失敗時回只含內建 profile 的預設內容。
        /// 內建 profile 一律補齊：OmniNav / Classic 唯讀，每次重新同步自程式碼定義；
        /// OmniNavL 僅在缺漏時種子，已存在則保留使用者編輯。
        /// </summary>
        public static GamepadProfileData Load()
        {
            var data = new GamepadProfileData();
            var path = ProfilesPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject root)
                    {
                        data.DefaultProfileId = root["defaultProfileId"]?.GetValue<string>() ?? string.Empty;

                        if (root["profiles"] is JsonArray profArr)
                        {
                            foreach (var item in profArr)
                            {
                                if (item is JsonObject obj && ParseProfile(obj) is { } p)
                                    data.Profiles.Add(p);
                            }
                        }
                        if (root["assignments"] is JsonArray asnArr)
                        {
                            foreach (var item in asnArr)
                            {
                                if (item is JsonObject obj && ParseAssignment(obj) is { } a)
                                    data.Assignments.Add(a);
                            }
                        }
                    }
                }
                catch
                {
                    data = new GamepadProfileData();
                }
            }
            ApplyBuiltIns(data);
            return data;
        }

        /// <summary>若 profile 檔尚不存在，以內建內容建立一份（讓 C++ 端有檔可讀）。</summary>
        public static void EnsureInitialized()
        {
            var path = ProfilesPath;
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) Save(Load());
        }

        /// <summary>補齊／重新同步內建 profile，並讓內建排在清單前段。</summary>
        private static void ApplyBuiltIns(GamepadProfileData data)
        {
            var builtIns = GamepadBuiltInLayouts.BuiltInProfiles();
            var userProfiles = data.Profiles.Where(p => !IsBuiltInId(p.Id)).ToList();

            var ordered = new List<GamepadProfile>();
            foreach (var bi in builtIns)
            {
                if (bi.IsReadOnly)
                {
                    // OmniNav / Classic：永遠採用程式碼裡的權威版本（自我修復被竄改的檔案）
                    ordered.Add(bi);
                }
                else
                {
                    // OmniNavL：保留已存檔（可能被使用者編輯）的版本，缺漏才種子。
                    // 但強制 Layered.Enabled=true — OmniNavL 識別性質的一部分，
                    // 禁用即等同 OmniNav（避免舊版檔案殘留 enabled=false 造成混淆）。
                    var stored = data.Profiles.FirstOrDefault(p => p.Id == bi.Id);
                    if (stored != null && stored.Id == GamepadBuiltInLayouts.OmniNavLId)
                    {
                        stored.Layered.Enabled = true;
                    }
                    ordered.Add(stored ?? bi);
                }
            }
            ordered.AddRange(userProfiles);
            data.Profiles = ordered;

            if (string.IsNullOrEmpty(data.DefaultProfileId) ||
                !data.Profiles.Any(p => p.Id == data.DefaultProfileId))
            {
                data.DefaultProfileId = GamepadBuiltInLayouts.OmniNavLId;
            }
        }

        /// <summary>以 id 取得 profile；不存在回 null。</summary>
        public static GamepadProfile? GetProfileById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Load().Profiles.FirstOrDefault(p => p.Id == id);
        }

        /// <summary>
        /// 解析前景 App 應套用的 profile：先查指派，未指派則回預設 profile。
        /// 內建 profile 必定存在，故回傳不為 null。
        /// </summary>
        public static GamepadProfile ResolveProfileForApp(AppId? app)
        {
            var data = Load();
            if (app != null)
            {
                var asn = data.Assignments.FirstOrDefault(a => SameTarget(a.AppId, app));
                if (asn != null)
                {
                    var assigned = data.Profiles.FirstOrDefault(p => p.Id == asn.ProfileId);
                    if (assigned != null) return assigned;
                }
            }
            return data.Profiles.FirstOrDefault(p => p.Id == data.DefaultProfileId)
                   ?? data.Profiles.First();
        }

        // ── 寫 ────────────────────────────────────────────────────────────────

        /// <summary>覆寫整個 store。PublisherCacheFolder 取不到時回 false。</summary>
        public static bool Save(GamepadProfileData data)
        {
            var path = ProfilesPath;
            if (string.IsNullOrEmpty(path) || data == null) return false;
            try
            {
                var root = new JsonObject
                {
                    ["version"] = SchemaVersion,
                    ["defaultProfileId"] = data.DefaultProfileId ?? GamepadBuiltInLayouts.OmniNavLId,
                    ["profiles"] = new JsonArray(data.Profiles.Where(p => p != null).Select(SerializeProfile).ToArray()),
                    ["assignments"] = new JsonArray(data.Assignments.Where(a => a != null).Select(SerializeAssignment).ToArray())
                };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, root.ToJsonString(opts), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>新增或覆寫一個 profile（依 Id）。內建唯讀 profile（OmniNav/Classic）拒絕寫入。</summary>
        public static bool UpsertProfile(GamepadProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id)) return false;
            if (profile.Id == GamepadBuiltInLayouts.OmniNavId ||
                profile.Id == GamepadBuiltInLayouts.ClassicId) return false;

            var data = Load();
            int idx = data.Profiles.FindIndex(p => p.Id == profile.Id);
            if (idx >= 0) data.Profiles[idx] = profile;
            else data.Profiles.Add(profile);
            return Save(data);
        }

        /// <summary>刪除一個 profile（內建 profile 不可刪）。連帶移除指向它的指派；若為預設則回退到 OmniNavL。</summary>
        public static bool DeleteProfile(string id)
        {
            if (string.IsNullOrEmpty(id) || IsBuiltInId(id)) return false;
            var data = Load();
            int removed = data.Profiles.RemoveAll(p => p.Id == id);
            if (removed == 0) return true;
            data.Assignments.RemoveAll(a => a.ProfileId == id);
            if (data.DefaultProfileId == id)
                data.DefaultProfileId = GamepadBuiltInLayouts.OmniNavLId;
            return Save(data);
        }

        /// <summary>設定預設 profile。id 不存在時不變更，回 false。</summary>
        public static bool SetDefaultProfile(string id)
        {
            var data = Load();
            if (!data.Profiles.Any(p => p.Id == id)) return false;
            data.DefaultProfileId = id;
            return Save(data);
        }

        /// <summary>把一個 App 指派到某 profile（取代該 App 既有指派）。黑名單 App 或未知 profile 回 false。</summary>
        public static bool SetAssignment(AppId appId, string profileId)
        {
            if (appId == null || string.IsNullOrEmpty(profileId)) return false;
            if (IsBlacklisted(appId)) return false;
            var data = Load();
            if (!data.Profiles.Any(p => p.Id == profileId)) return false;
            data.Assignments.RemoveAll(a => SameTarget(a.AppId, appId));
            data.Assignments.Add(new ProfileAssignment { AppId = appId, ProfileId = profileId });
            return Save(data);
        }

        /// <summary>移除某 App 的指派。不存在時視為成功。</summary>
        public static bool RemoveAssignment(AppId appId)
        {
            if (appId == null) return false;
            var data = Load();
            if (data.Assignments.RemoveAll(a => SameTarget(a.AppId, appId)) == 0) return true;
            return Save(data);
        }

        // ── App 比對 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 兩個 AppId 是否指向同一指派槽。
        /// Aumid：Kind + Value 相同即可。Process：Kind + Value + 正規化 FullPath 皆相同。
        /// </summary>
        private static bool SameTarget(AppId a, AppId b)
        {
            if (a == null || b == null) return false;
            if (!a.Matches(b)) return false;
            if (a.Kind == IdKind.Aumid) return true;
            string? pa = AppId.NormalizePath(a.FullPath);
            string? pb = AppId.NormalizePath(b.FullPath);
            return string.Equals(pa, pb, StringComparison.Ordinal);
        }

        // ── 序列化 / 反序列化：profile ────────────────────────────────────────

        private static GamepadProfile? ParseProfile(JsonObject obj)
        {
            string id = obj["id"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(id)) return null;

            var prof = new GamepadProfile
            {
                Id = id,
                Name = obj["name"]?.GetValue<string>() ?? id,
                IsBuiltIn = obj["builtIn"]?.GetValue<bool>() ?? false,
                IsReadOnly = obj["readOnly"]?.GetValue<bool>() ?? false,
                CursorSpeedPercent = obj["cursorSpeedPercent"]?.GetValue<int>() ?? 100,
                DpadAutoRepeat = obj["dpadAutoRepeat"]?.GetValue<bool>() ?? true,
            };

            if (obj["layered"] is JsonObject layeredObj)
            {
                prof.Layered.Enabled = layeredObj["enabled"]?.GetValue<bool>() ?? false;
                if (Enum.TryParse<GamepadInputId>(layeredObj["triggerKey"]?.GetValue<string>(), true, out var tk))
                    prof.Layered.TriggerKey = tk;
                if (Enum.TryParse<ProfileActivationMode>(layeredObj["activationMode"]?.GetValue<string>(), true, out var am))
                    prof.Layered.ActivationMode = am;
            }

            if (obj["bindings"] is JsonObject bindings)
            {
                foreach (var kv in bindings)
                {
                    if (kv.Value is not JsonObject actionObj) continue;
                    if (!Enum.TryParse<GamepadInputId>(kv.Key, ignoreCase: true, out var inputId)) continue;
                    if (ParseAction(actionObj) is { } act) prof.Bindings[inputId] = act;
                }
            }
            return prof;
        }

        private static GamepadAction ParseAction(JsonObject obj)
        {
            string kindStr = obj["kind"]?.GetValue<string>() ?? string.Empty;
            if (!Enum.TryParse<GamepadActionKind>(kindStr, ignoreCase: true, out var kind))
                return new GamepadAction { Kind = GamepadActionKind.None };

            var act = new GamepadAction { Kind = kind };
            switch (kind)
            {
                case GamepadActionKind.KeyTap:
                case GamepadActionKind.KeyHold:
                    act.Vk = obj["vk"]?.GetValue<int>() ?? 0;
                    break;
                case GamepadActionKind.KeyCombo:
                    act.Vk = obj["vk"]?.GetValue<int>() ?? 0;
                    if (obj["mods"] is JsonArray modsArr)
                    {
                        foreach (var m in modsArr)
                        {
                            string ms = m?.GetValue<string>() ?? string.Empty;
                            if (string.Equals(ms, "Ctrl", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Ctrl;
                            if (string.Equals(ms, "Shift", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Shift;
                            if (string.Equals(ms, "Alt", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Alt;
                            if (string.Equals(ms, "Win", StringComparison.OrdinalIgnoreCase)) act.Mods |= GamepadModifier.Win;
                        }
                    }
                    break;
                case GamepadActionKind.MouseButton:
                    string which = obj["which"]?.GetValue<string>() ?? "Left";
                    act.Which = string.Equals(which, "Right", StringComparison.OrdinalIgnoreCase) ? GamepadMouseWhich.Right
                              : string.Equals(which, "Middle", StringComparison.OrdinalIgnoreCase) ? GamepadMouseWhich.Middle
                              : GamepadMouseWhich.Left;
                    break;
                case GamepadActionKind.MouseWheel:
                    string dir = obj["dir"]?.GetValue<string>() ?? "Up";
                    act.Dir = string.Equals(dir, "Down", StringComparison.OrdinalIgnoreCase) ? GamepadWheelDir.Down
                            : string.Equals(dir, "Left", StringComparison.OrdinalIgnoreCase) ? GamepadWheelDir.Left
                            : string.Equals(dir, "Right", StringComparison.OrdinalIgnoreCase) ? GamepadWheelDir.Right
                            : GamepadWheelDir.Up;
                    break;
                case GamepadActionKind.TouchKeyboard:
                    string vkb = obj["vkb"]?.GetValue<string>() ?? "Com";
                    act.Vkb = string.Equals(vkb, "Osk", StringComparison.OrdinalIgnoreCase) ? VkbMethod.Osk : VkbMethod.Com;
                    break;
            }
            return act;
        }

        private static JsonObject SerializeProfile(GamepadProfile prof)
        {
            var bindings = new JsonObject();
            foreach (var kv in prof.Bindings)
            {
                if (kv.Value == null || kv.Value.Kind == GamepadActionKind.None) continue;
                bindings[kv.Key.ToString()] = SerializeAction(kv.Value);
            }

            return new JsonObject
            {
                ["id"] = prof.Id,
                ["name"] = prof.Name ?? string.Empty,
                ["builtIn"] = prof.IsBuiltIn,
                ["readOnly"] = prof.IsReadOnly,
                ["cursorSpeedPercent"] = prof.CursorSpeedPercent,
                ["dpadAutoRepeat"] = prof.DpadAutoRepeat,
                ["layered"] = new JsonObject
                {
                    ["enabled"] = prof.Layered.Enabled,
                    ["triggerKey"] = prof.Layered.TriggerKey.ToString(),
                    ["activationMode"] = prof.Layered.ActivationMode.ToString()
                },
                ["bindings"] = bindings
            };
        }

        private static JsonObject SerializeAction(GamepadAction a)
        {
            var obj = new JsonObject { ["kind"] = a.Kind.ToString() };
            switch (a.Kind)
            {
                case GamepadActionKind.KeyTap:
                case GamepadActionKind.KeyHold:
                    obj["vk"] = a.Vk;
                    break;
                case GamepadActionKind.KeyCombo:
                    obj["vk"] = a.Vk;
                    var mods = new JsonArray();
                    if ((a.Mods & GamepadModifier.Ctrl) != 0) mods.Add((JsonNode)JsonValue.Create("Ctrl"));
                    if ((a.Mods & GamepadModifier.Shift) != 0) mods.Add((JsonNode)JsonValue.Create("Shift"));
                    if ((a.Mods & GamepadModifier.Alt) != 0) mods.Add((JsonNode)JsonValue.Create("Alt"));
                    if ((a.Mods & GamepadModifier.Win) != 0) mods.Add((JsonNode)JsonValue.Create("Win"));
                    obj["mods"] = mods;
                    break;
                case GamepadActionKind.MouseButton:
                    obj["which"] = a.Which.ToString();
                    break;
                case GamepadActionKind.MouseWheel:
                    obj["dir"] = a.Dir.ToString();
                    break;
                case GamepadActionKind.TouchKeyboard:
                    obj["vkb"] = a.Vkb.ToString();
                    break;
            }
            return obj;
        }

        // ── 序列化 / 反序列化：assignment ─────────────────────────────────────

        private static ProfileAssignment? ParseAssignment(JsonObject obj)
        {
            if (obj["appId"] is not JsonObject appIdObj) return null;
            string profileId = obj["profileId"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(profileId)) return null;

            string kindStr = appIdObj["kind"]?.GetValue<string>() ?? "process";
            string value = appIdObj["value"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return null;
            string? fullPath = appIdObj["fullPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fullPath) || !AppId.IsValidFullPath(fullPath)) fullPath = null;

            return new ProfileAssignment
            {
                AppId = new AppId
                {
                    Kind = string.Equals(kindStr, "aumid", StringComparison.OrdinalIgnoreCase) ? IdKind.Aumid : IdKind.Process,
                    Value = value,
                    FullPath = fullPath
                },
                ProfileId = profileId
            };
        }

        private static JsonObject SerializeAssignment(ProfileAssignment a)
        {
            var appIdObj = new JsonObject
            {
                ["kind"] = a.AppId.Kind == IdKind.Aumid ? "aumid" : "process",
                ["value"] = a.AppId.Value ?? string.Empty
            };
            if (!string.IsNullOrEmpty(a.AppId.FullPath))
                appIdObj["fullPath"] = a.AppId.FullPath;

            return new JsonObject
            {
                ["appId"] = appIdObj,
                ["profileId"] = a.ProfileId ?? string.Empty
            };
        }
    }
}
