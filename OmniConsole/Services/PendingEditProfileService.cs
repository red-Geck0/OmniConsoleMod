using System;

namespace OmniConsole.Services
{
    /// <summary>
    /// 跨 process 邊界傳遞「待編輯手把 profile」請求的暫存層。
    /// Program.cs 收到 omniconsole://edit-gamepad-profile?profileId=... 時 Stash 進 LocalSettings；
    /// SettingsPage 進手把映射分頁時 TryConsume 取出並清除。
    /// </summary>
    public static class PendingEditProfileService
    {
        /// <summary>Protocol query string 字元數上限，超過直接放棄解析。</summary>
        private const int MaxProtocolQueryLength = 2048;

        /// <summary>profileId 字元數上限，超過視為非法。</summary>
        private const int MaxProfileIdLength = 128;

        private const string KeyProfileId = "PendingEditProfileId";

        /// <summary>
        /// 解析 omniconsole://edit-gamepad-profile?profileId=... 的 query string，寫入 LocalSettings 供 SettingsPage 取用。
        /// Query 總長超過 MaxProtocolQueryLength、profileId 超過 MaxProfileIdLength 皆放棄。
        /// </summary>
        public static void Stash(Uri uri)
        {
            try
            {
                string query = uri.Query ?? string.Empty;
                if (query.Length > MaxProtocolQueryLength)
                {
                    DebugLogger.Log($"→ PendingEditProfileService.Stash: query length {query.Length} exceeds {MaxProtocolQueryLength}, ignored");
                    return;
                }
                if (query.StartsWith("?")) query = query.Substring(1);

                string profileId = string.Empty;
                foreach (var pair in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq);
                    string val = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    if (string.Equals(key, "profileId", StringComparison.OrdinalIgnoreCase))
                        profileId = val;
                }

                if (!string.IsNullOrEmpty(profileId) && profileId.Length <= MaxProfileIdLength)
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values[KeyProfileId] = profileId;
                    DebugLogger.Log($"→ Stashed pending edit profile: profileId={profileId}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"→ PendingEditProfileService.Stash EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>取出先前 Stash 的 profileId 並從 LocalSettings 移除；無暫存或解析失敗回 null。</summary>
        public static string? TryConsume()
        {
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (local.Values.TryGetValue(KeyProfileId, out var obj) && obj is string s && !string.IsNullOrEmpty(s))
                {
                    local.Values.Remove(KeyProfileId);
                    return s;
                }
            }
            catch
            {
                // 取用失敗視為無暫存
            }
            return null;
        }
    }
}
