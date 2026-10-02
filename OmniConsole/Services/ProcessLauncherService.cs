using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace OmniConsole.Services
{
    /// <summary>
    /// 泛用的平台啟動服務，依據 <see cref="PlatformDefinition"/> 中定義的策略清單依序嘗試啟動。
    /// 本服務不包含任何平台特定的硬編碼邏輯，所有平台參數均來自 <see cref="PlatformCatalog"/>。
    /// </summary>
    public static class ProcessLauncherService
    {
        private static readonly ResourceLoader _resourceLoader = new();

        /// <summary>單一啟動策略的執行結果。</summary>
        private enum StrategyResult
        {
            /// <summary>平台已被叫起來。</summary>
            Launched,

            /// <summary>這個策略沒成功（目標不存在、登錄值缺漏、URI 未登錄…），可以再試下一個。</summary>
            Failed,

            /// <summary>使用者在 UAC 提示上按了取消。那是對這次啟動的明確表態，不該再往下試。</summary>
            ElevationDeclined,
        }

        /// <summary>
        /// 這個策略此刻能不能以系統管理員身分啟動。
        ///   Registry / Executable：由本程式直接建立行程，用 runas 提權（每次一個 UAC）。
        ///   PackagedApp：走不了 runas（AppModel 啟動的 IL 由系統決定），改由提權版 PhantomKey 代打
        ///     （Route B，執行期免 UAC）。條件是「系統管理員程式支援」已安裝，且該套件解析得出
        ///     full-trust exe（純 UWP 如 Xbox App 解析不到）。
        ///   ProtocolUri：一律不行（純交給 Shell）。
        /// </summary>
        private static bool CanElevate(LaunchStrategy strategy) => strategy.Type switch
        {
            LaunchStrategyType.Registry or LaunchStrategyType.Executable => true,
            LaunchStrategyType.PackagedApp =>
                ElevatedInputService.IsElevatedRuntimeAvailable()
                && ElevatedLaunchService.CanElevatePackaged(strategy.PackageFamilyName),
            _ => false,
        };

        /// <summary>
        /// 平台是否含「只要安裝了系統管理員程式支援就能提權」的封裝策略，而目前尚未安裝支援。
        /// 供設定頁提示使用者：這個開關對此平台需要先裝支援才會生效（有別於「這個平台根本無法提權」）。
        /// </summary>
        public static bool NeedsAdminSupportToElevate(PlatformDefinition platform)
        {
            if (ElevatedInputService.IsElevatedRuntimeAvailable()) return false;
            return platform.LaunchStrategies.Any(s =>
                s.Type == LaunchStrategyType.PackagedApp
                && ElevatedLaunchService.CanElevatePackaged(s.PackageFamilyName));
        }

        /// <summary>
        /// 平台是否有任何「提權得了」的啟動策略，供設定頁判斷要不要提醒使用者
        /// 這個開關對目前選的平台不會有作用。
        /// </summary>
        public static bool HasElevatableStrategy(PlatformDefinition platform) =>
            platform.LaunchStrategies.Any(CanElevate);

        /// <summary>
        /// 依序嘗試平台定義中的啟動策略，第一個成功即停止。
        ///
        /// 開啟「以系統管理員身分啟動」時分成兩輪：先只跑提權得了的策略，
        /// 全軍覆沒才退回完整清單以一般權限啟動。不分兩輪的話，像 Steam Big Picture 這種
        /// 第一順位是 steam:// 的平台永遠會在 ProtocolUri 那一步就成功收工，
        /// 後面那個真的能提權的 Registry 策略根本輪不到，開關等於沒有作用。
        /// </summary>
        public static async Task<bool> LaunchPlatformAsync(PlatformDefinition platform)
        {
            // 設定一次讀完再往下帶，避免每個策略各查一次 LocalSettings。
            bool elevated = SettingsService.GetLaunchPlatformElevated();
            DebugLogger.Log($"[ProcessLauncher] Launching platform: {platform.Id} ({platform.LaunchStrategies.Count} strategies defined, elevated={elevated})");

            if (elevated)
            {
                var result = await RunStrategiesAsync(platform, elevated: true, onlyElevatable: true);
                if (result == StrategyResult.Launched) return true;

                if (result == StrategyResult.ElevationDeclined)
                {
                    // 使用者按了取消就是不要。不要繞過去改用一般權限把平台開起來——
                    // 那等於無視他剛剛的回答。
                    DebugLogger.Log($"[ProcessLauncher] {platform.Id}: elevation declined by user, launch aborted.");
                    return false;
                }

                DebugLogger.Log($"[ProcessLauncher] {platform.Id}: no elevatable strategy succeeded, falling back to a normal launch.");
            }

            if (await RunStrategiesAsync(platform, elevated: false, onlyElevatable: false) == StrategyResult.Launched)
                return true;

            DebugLogger.Log($"[ProcessLauncher] {platform.Id}: All launch strategies failed.");
            return false;
        }

        /// <summary>
        /// 跑一輪啟動策略。<paramref name="onlyElevatable"/> 為 true 時略過提權不了的那些。
        /// </summary>
        private static async Task<StrategyResult> RunStrategiesAsync(
            PlatformDefinition platform, bool elevated, bool onlyElevatable)
        {
            int total = platform.LaunchStrategies.Count;
            for (int i = 0; i < total; i++)
            {
                var strategy = platform.LaunchStrategies[i];
                if (onlyElevatable && !CanElevate(strategy))
                {
                    DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1}/{total} ({strategy.Type}): skipped, cannot be elevated.");
                    continue;
                }

                DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1}/{total}: Attempting {strategy.Type}{(elevated ? " (elevated)" : "")}...");
                var result = await ExecuteStrategyAsync(strategy, platform.Id, elevated);

                if (result == StrategyResult.Launched)
                {
                    DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1} ({strategy.Type}) succeeded.");
                    return StrategyResult.Launched;
                }
                if (result == StrategyResult.ElevationDeclined) return result;

                DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1} ({strategy.Type}) failed.");
            }
            return StrategyResult.Failed;
        }

        /// <summary>
        /// 取得平台的在地化顯示名稱。
        /// 優先從 .resw 資源檔讀取，若失敗則回退到 Id。
        /// </summary>
        public static string GetPlatformDisplayName(PlatformDefinition platform)
        {
            // 使用者自訂平台的 DisplayNameKey 以 __user__ 開頭，直接從 UserPlatformStore 取名稱
            if (platform.DisplayNameKey.StartsWith("__user__"))
            {
                var entry = UserPlatformStore.FindEntryById(platform.Id);
                return entry?.DisplayName ?? platform.Id;
            }

            // 掃描到的 FSE Home App 候選：名稱來自套件 manifest，同樣不在 .resw 裡
            if (platform.DisplayNameKey.StartsWith(GamingHomeAppEntry.DisplayNameKeyPrefix))
            {
                var entry = GamingHomeAppStore.FindEntryById(platform.Id);
                return entry?.DisplayName ?? platform.Id;
            }

            try
            {
                string? name = _resourceLoader.GetString(platform.DisplayNameKey);
                return !string.IsNullOrEmpty(name) ? name : platform.Id;
            }
            catch
            {
                return platform.Id;
            }
        }

        /// <summary>
        /// 檢查平台是否「看起來已經在跑」（僅涵蓋 Executable 策略；其餘策略類型一律回 false，
        /// 也就是「無法判斷」而非「確定沒在跑」）。
        /// 用途：Xbox Game Bar「Home」鍵在平台仍在前景執行時會讓 OmniConsole 冷啟動一個全新行程
        /// （因為每次成功啟動平台後 OmniConsole 都會整個結束，見 App.ExitApp），此時若不特別檢查，
        /// 開機影片等「啟動」相關的一次性畫面會被誤判成又一次全新啟動而重播。
        /// 比對邏輯與 PhantomKeyService.IsRunning() 相同：用執行檔名稱粗篩候選行程，
        /// 再以 MainModule.FileName 比對完整路徑，避免同名但不同來源的行程誤判。
        /// </summary>
        public static bool IsPlatformRunning(PlatformDefinition platform)
        {
            foreach (var strategy in platform.LaunchStrategies)
            {
                if (strategy.Type != LaunchStrategyType.Executable) continue;
                if (string.IsNullOrEmpty(strategy.ExecutableName)) continue;

                string exePath = Environment.ExpandEnvironmentVariables(strategy.ExecutableName);
                if (!Path.IsPathRooted(exePath) && strategy.SearchPaths != null)
                {
                    foreach (string dir in strategy.SearchPaths)
                    {
                        string expandedDir = Environment.ExpandEnvironmentVariables(dir);
                        string candidate = Path.Combine(expandedDir, exePath);
                        if (File.Exists(candidate)) { exePath = candidate; break; }
                    }
                }
                if (!Path.IsPathRooted(exePath)) continue; // 無法解析出完整路徑，無法可靠比對

                string processName = Path.GetFileNameWithoutExtension(exePath);
                try
                {
                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (exePath.Equals(proc.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch { /* MainModule 存取偶爾因權限/行程剛結束而失敗，略過該候選繼續檢查其它行程 */ }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[ProcessLauncher] IsPlatformRunning check failed for {platform.Id}: {ex.Message}");
                }
            }
            return false;
        }

        /// <summary>
        /// 檢查指定平台是否已安裝（不觸發啟動）。
        /// </summary>
        public static Task<bool> CheckPlatformAvailableAsync(PlatformDefinition platform)
        {
            var s = platform.AvailabilityStrategy;
            return s.Type switch
            {
                LaunchStrategyType.ProtocolUri => IsUriSupportedAsync(s.Uri!),
                LaunchStrategyType.Registry => Task.FromResult(IsRegistryPathPresent(s)),
                LaunchStrategyType.PackagedApp => IsPackagedAppInstalledAsync(s),
                LaunchStrategyType.Executable => Task.FromResult(IsExecutableAvailable(s)),
                _ => Task.FromResult(false),
            };
        }

        // ── 策略執行 ──────────────────────────────────────────────────────────

        /// <param name="elevated">
        /// 是否以系統管理員身分啟動（Settings ＞ 進階 的開關）。
        /// 只有 <see cref="CanElevate"/> 認可的策略吃得到這個旗標；呼叫端會先篩過，
        /// 這裡不再重複判斷。
        /// </param>
        private static async Task<StrategyResult> ExecuteStrategyAsync(LaunchStrategy strategy, string platformId, bool elevated)
        {
            switch (strategy.Type)
            {
                case LaunchStrategyType.ProtocolUri:
                    return ToResult(await TryLaunchUriAsync(strategy.Uri!, platformId));
                case LaunchStrategyType.Registry:
                    return TryLaunchFromRegistry(strategy, platformId, elevated);
                case LaunchStrategyType.Executable:
                    return TryLaunchExecutable(strategy, platformId, elevated);
                case LaunchStrategyType.PackagedApp:
                    // 提權 + 支援可用 + 解析得出 full-trust exe → 交給提權版 PhantomKey 代打（免 UAC）。
                    // 委派成功即視為已啟動；解析失敗（純 UWP）或支援不可用時 TryLaunchElevated 回 false，
                    // 退回一般權限的 AppModel 啟動，讓平台至少開得起來。
                    if (elevated
                        && !string.IsNullOrEmpty(strategy.PackageFamilyName)
                        && ElevatedInputService.IsElevatedRuntimeAvailable()
                        && ElevatedLaunchService.TryLaunchElevatedPackaged(strategy.PackageFamilyName!))
                    {
                        return StrategyResult.Launched;
                    }
                    return ToResult(await TryLaunchPackagedAppAsync(strategy, platformId));
                default:
                    return StrategyResult.Failed;
            }
        }

        private static StrategyResult ToResult(bool launched) =>
            launched ? StrategyResult.Launched : StrategyResult.Failed;

        /// <summary>
        /// 透過 Protocol URI 啟動，啟動前先確認 URI handler 已登錄。
        /// </summary>
        private static async Task<bool> TryLaunchUriAsync(string uriString, string platformId)
        {
            try
            {
                DebugLogger.Log($"[ProcessLauncher]   Target URI: {uriString}");
                var uri = new Uri(uriString);

                // 確認 URI handler 已安裝，避免跳出「在 Store 中尋找應用程式」對話方塊
                var status = await Launcher.QueryUriSupportAsync(
                    uri, LaunchQuerySupportType.Uri);

                if (status != LaunchQuerySupportStatus.Available)
                {
                    DebugLogger.Log($"[ProcessLauncher]   URI not supported: {status}");
                    return false;
                }

                bool success = await Launcher.LaunchUriAsync(uri);
                if (success)
                    DebugLogger.Log($"[ProcessLauncher]   LaunchUriAsync call succeeded.");
                return success;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   URI launch exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 從登錄機碼讀取安裝路徑，直接啟動執行檔。
        /// ExecutableName 有值時視為目錄 + 檔名組合；為 null 時視登錄值本身為完整執行檔路徑。
        /// </summary>
        private static StrategyResult TryLaunchFromRegistry(LaunchStrategy strategy, string platformId, bool elevated)
        {
            try
            {
                DebugLogger.Log($"[ProcessLauncher]   Registry lookup: {strategy.RegistryRoot}\\{strategy.RegistrySubKey}\\{strategy.RegistryValueName}");
                string? registryValue = ReadRegistryValue(
                    strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!);

                if (string.IsNullOrEmpty(registryValue))
                {
                    DebugLogger.Log("[ProcessLauncher]   Registry value is empty or missing.");
                    return StrategyResult.Failed;
                }

                string exePath;
                if (strategy.ParseCommandToDirectory)
                {
                    // 登錄值為命令字串（例如 URI handler 的 shell\open\command），
                    // 解析出執行檔路徑，取其目錄後與 ExecutableName 組合
                    string? parsedExe = ParseExePathFromCommand(registryValue);
                    string? dir = parsedExe is not null ? Path.GetDirectoryName(parsedExe) : null;
                    if (string.IsNullOrEmpty(dir))
                    {
                        DebugLogger.Log("[ProcessLauncher]   Failed to parse directory from command string.");
                        return StrategyResult.Failed;
                    }
                    exePath = strategy.ExecutableName is not null
                        ? Path.Combine(dir, strategy.ExecutableName)
                        : dir;
                }
                else
                {
                    exePath = strategy.ExecutableName is not null
                        ? Path.Combine(registryValue, strategy.ExecutableName)
                        : registryValue;
                }

                DebugLogger.Log($"[ProcessLauncher]   Resolved registry path: {exePath}");
                return StartProcess(exePath, strategy.Arguments ?? "", elevated);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Registry launch exception: {ex.Message}");
                return StrategyResult.Failed;
            }
        }

        /// <summary>
        /// 透過 PackageManager 找到已安裝的封裝應用程式並啟動。
        /// </summary>
        private static async Task<bool> TryLaunchPackagedAppAsync(LaunchStrategy strategy, string platformId)
        {
            try
            {
                string identifier = strategy.PackageFamilyName ?? strategy.PackageName ?? "Unknown";
                DebugLogger.Log($"[ProcessLauncher]   Target Packaged App: {identifier}");

                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = !string.IsNullOrEmpty(strategy.PackageFamilyName)
                    ? pm.FindPackagesForUser(string.Empty, strategy.PackageFamilyName)
                    : pm.FindPackagesForUser(string.Empty, strategy.PackageName!, strategy.Publisher!);

                var packageList = packages.ToList();
                if (!packageList.Any())
                {
                    DebugLogger.Log("[ProcessLauncher]   No matching package found for current user.");
                    return false;
                }

                foreach (var package in packageList)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    if (entries.Count > 0 && await entries[0].LaunchAsync())
                    {
                        DebugLogger.Log($"[ProcessLauncher]   Successfully launched: {package.Id.FullName}");
                        return true;
                    }
                }
                DebugLogger.Log("[ProcessLauncher]   Found package but failed to launch any app entry.");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Packaged App launch exception: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 直接啟動指定的執行檔。
        /// 支援絕對路徑；若僅指定檔名，會優先嘗試在定義的 SearchPaths 尋找，
        /// 若均未找到或未定義，則交由作業系統的 PATH 或 App Paths 機制尋找啟動。
        /// </summary>
        private static StrategyResult TryLaunchExecutable(LaunchStrategy strategy, string platformId, bool elevated)
        {
            try
            {
                if (string.IsNullOrEmpty(strategy.ExecutableName))
                {
                    DebugLogger.Log("[ProcessLauncher]   ExecutableName is empty.");
                    return StrategyResult.Failed;
                }

                string exeName = Environment.ExpandEnvironmentVariables(strategy.ExecutableName);
                string launchPath = exeName;

                if (!Path.IsPathRooted(exeName) && strategy.SearchPaths != null)
                {
                    DebugLogger.Log($"[ProcessLauncher]   Searching for {exeName} in {strategy.SearchPaths.Length} paths...");
                    foreach (string dir in strategy.SearchPaths)
                    {
                        string expandedDir = Environment.ExpandEnvironmentVariables(dir);
                        string fullPath = Path.Combine(expandedDir, exeName);
                        if (File.Exists(fullPath))
                        {
                            launchPath = fullPath;
                            DebugLogger.Log($"[ProcessLauncher]   Found at: {launchPath}");
                            break;
                        }
                    }
                }

                // 若為絕對路徑，則可以事前檢查檔案是否存在
                if (Path.IsPathRooted(launchPath) && !File.Exists(launchPath))
                {
                    DebugLogger.Log($"[ProcessLauncher]   Executable not found at rooted path: {launchPath}");
                    return StrategyResult.Failed;
                }

                return StartProcess(launchPath, strategy.Arguments ?? "", elevated);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Executable launch exception: {ex.Message}");
                return StrategyResult.Failed;
            }
        }

        // ── 可用性查詢輔助方法 ────────────────────────────────────────────────

        /// <summary>
        /// 查詢系統是否有已登錄的 URI handler 可處理指定 URI scheme，
        /// 不實際啟動應用程式。
        /// </summary>
        public static async Task<bool> IsUriSupportedAsync(string uriString)
        {
            try
            {
                var uri = new Uri(uriString);
                var status = await Launcher.QueryUriSupportAsync(
                    uri, LaunchQuerySupportType.Uri);

                if (status != LaunchQuerySupportStatus.Available)
                    return false;

                // 若作業系統回報 Available，進一步檢查登錄檔中的執行檔是否存在，避免解除安裝後殘留的假象
                return IsUriHandlerExecutableValid(uri.Scheme);
            }
            catch { return false; }
        }

        /// <summary>
        /// 嘗試檢查 URI Scheme 在登錄檔中登錄的執行檔是否真實存在。
        /// 針對傳統桌面應用程式會解析絕對路徑並檢查檔案；若為 UWP 應用程式或無法解析絕對路徑時，預設回傳 true 交由系統處理。
        /// </summary>
        private static bool IsUriHandlerExecutableValid(string scheme)
        {
            try
            {
                // 檢查 HKCU 與 HKLM 的 Software\Classes\{scheme}\shell\open\command
                string? command = ReadRegistryValue("HKCU", $@"Software\Classes\{scheme}\shell\open\command", "")
                               ?? ReadRegistryValue("HKLM", $@"Software\Classes\{scheme}\shell\open\command", "");

                if (string.IsNullOrEmpty(command))
                {
                    // 有些可能是 UWP 應用程式，沒有傳統的 command 機碼，直接假設為 true 交給系統處理
                    return true;
                }

                string? exePath = ParseExePathFromCommand(command);
                if (string.IsNullOrEmpty(exePath))
                    return true;

                exePath = Environment.ExpandEnvironmentVariables(exePath);

                // 如果能解析出絕對路徑，則真實檢查檔案是否存在
                if (Path.IsPathRooted(exePath))
                {
                    bool exists = File.Exists(exePath);
                    if (!exists)
                    {
                        DebugLogger.Log($"[ProcessLauncher] Ghost URI Handler '{scheme}://' detected! Executable missing: {exePath}");
                    }
                    else
                    {
                        DebugLogger.Log($"[ProcessLauncher] Valid URI Handler '{scheme}://' confirmed at: {exePath}");
                    }
                    return exists;
                }

                // 如果無法解析為絕對路徑，保險起見當作存在
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher] URI handler validation failed for '{scheme}://': {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 檢查登錄機碼對應的字串值是否存在且非空，
        /// 用以判斷平台是否已安裝（Registry 策略）。
        /// </summary>
        private static bool IsRegistryPathPresent(LaunchStrategy strategy) =>
            !string.IsNullOrEmpty(ReadRegistryValue(
                strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!));

        /// <summary>
        /// 以 PackageManager 搜尋封裝應用程式，判斷是否已為目前使用者安裝。
        /// 優先使用 PackageFamilyName（雙參數多載），否則使用 PackageName + Publisher（三參數多載）。
        /// </summary>
        private static Task<bool> IsPackagedAppInstalledAsync(LaunchStrategy strategy)
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = !string.IsNullOrEmpty(strategy.PackageFamilyName)
                    ? pm.FindPackagesForUser(string.Empty, strategy.PackageFamilyName)
                    : pm.FindPackagesForUser(string.Empty, strategy.PackageName!, strategy.Publisher!);
                return Task.FromResult(packages.Any());
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>
        /// 檢查執行檔策略的目標是否存在。
        /// 絕對路徑直接檢查檔案；相對路徑或純檔名則嘗試 SearchPaths 後視為可用。
        /// </summary>
        private static bool IsExecutableAvailable(LaunchStrategy strategy)
        {
            if (string.IsNullOrEmpty(strategy.ExecutableName)) return false;

            string exePath = Environment.ExpandEnvironmentVariables(strategy.ExecutableName);

            if (Path.IsPathRooted(exePath))
                return File.Exists(exePath);

            // 非絕對路徑：嘗試 SearchPaths
            if (strategy.SearchPaths != null)
            {
                foreach (string dir in strategy.SearchPaths)
                {
                    string expandedDir = Environment.ExpandEnvironmentVariables(dir);
                    if (File.Exists(Path.Combine(expandedDir, exePath)))
                        return true;
                }
            }

            // 非絕對路徑且無 SearchPaths 或未找到：無法驗證，視為不可用
            return false;
        }

        // ── 通用輔助方法 ──────────────────────────────────────────────────────

        /// <summary>
        /// 從命令字串（例如 URI handler 的 shell\open\command 值）解析出執行檔路徑。
        /// 支援帶引號（"C:\...\app.exe" "%1"）與不帶引號（C:\...\app.exe %1）兩種格式。
        /// </summary>
        private static string? ParseExePathFromCommand(string command)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int endQuote = command.IndexOf("\"", 1);
                if (endQuote > 1)
                    return command.Substring(1, endQuote - 1);
            }
            else
            {
                int spaceIndex = command.IndexOf(" ");
                return spaceIndex > 0 ? command.Substring(0, spaceIndex) : command;
            }
            return null;
        }

        /// <summary>
        /// 從 Windows 登錄機碼讀取字串值。
        /// </summary>
        private static string? ReadRegistryValue(string registryRoot, string subKeyPath, string valueName)
        {
            try
            {
                var rootKey = registryRoot.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                    ? Registry.CurrentUser
                    : Registry.LocalMachine;

                using var key = rootKey.OpenSubKey(subKeyPath);
                return key?.GetValue(valueName) as string;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher] Registry read failed ({registryRoot}\\{subKeyPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>取消 UAC 對話方塊時 ShellExecute 回報的錯誤碼（ERROR_CANCELLED）。</summary>
        private const int ErrorCancelled = 1223;

        /// <summary>
        /// 通用的絕對路徑程式啟動輔助方法。
        /// 呼叫前會嚴格檢查（File.Exists）檔案是否存在，因此不支援依賴系統 PATH 或 App Paths 的純檔名啟動。
        /// </summary>
        /// <param name="elevated">
        /// true 時以 runas 動詞啟動，也就是請 Shell 用系統管理員權杖建立行程。
        /// 本程式自己是一般權限，所以這一定會跳出 UAC 提示（安全桌面，只吃實體輸入）。
        /// 使用者在提示上按取消時回傳 false —— 這是他自己的決定，不是故障，
        /// 呼叫端照常往下一個策略試即可。
        /// </param>
        public static bool LaunchProcess(string filePath, string arguments = "", bool elevated = false) =>
            StartProcess(filePath, arguments, elevated) == StrategyResult.Launched;

        /// <summary>
        /// <see cref="LaunchProcess"/> 的內部版本，額外把「使用者取消 UAC」跟一般失敗分開回報。
        /// </summary>
        private static StrategyResult StartProcess(string filePath, string arguments, bool elevated)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    DebugLogger.Log($"[ProcessLauncher]   LaunchProcess: Executable not found: {filePath}");
                    return StrategyResult.Failed;
                }

                // 提權啟動：exe 位在「僅系統管理員可寫」位置且系統管理員程式支援可用時，交給已提權的
                // PhantomKey 代打（Route B，免 UAC）。使用者可寫位置（如 C:\Games、LocalAppData）的
                // exe 不走這條——讓提權行程去啟動可被竄改的檔案等於開提權後門，故退回下方 runas（跳 UAC）。
                if (elevated
                    && ElevatedInputService.IsElevatedRuntimeAvailable()
                    && ElevatedLaunchService.IsAdminOnlyLocation(filePath))
                {
                    ElevatedLaunchService.LaunchElevatedExe(filePath, arguments);
                    return StrategyResult.Launched;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                };
                if (elevated) startInfo.Verb = "runas";
                Process.Start(startInfo);
                DebugLogger.Log($"[ProcessLauncher]   Process launched{(elevated ? " (elevated)" : "")}: {filePath} {arguments}");
                return StrategyResult.Launched;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                DebugLogger.Log($"[ProcessLauncher]   Elevation declined by user: {filePath}");
                return StrategyResult.ElevationDeclined;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Process launch failed: {ex.Message}");
                return StrategyResult.Failed;
            }
        }
    }
}
