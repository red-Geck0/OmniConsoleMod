using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace OmniConsole.Services
{
    /// <summary>
    /// 以系統管理員身分啟動封裝應用程式（MSIX / packaged Win32），執行時不跳 UAC（Route B）。
    ///
    /// 為什麼 <see cref="ProcessLauncherService"/> 的 runas 路徑對封裝 App 沒用：
    ///   PackagedApp 策略走 AppListEntry.LaunchAsync()，由 AppModel 代為啟動，
    ///   目標行程的完整性等級由系統決定，我們指定不了；對 shell:AppsFolder 下 runas 也會被忽略。
    ///
    /// 做法：交給已提權常駐的 PhantomKey 代打。它在「系統管理員程式支援」安裝後跑在 High IL，
    /// 直接 CreateProcess 目標 exe（見 PhantomKey/ElevatedLaunch.cpp），子行程即繼承 High IL；
    /// 又因 exe 位在 WindowsApps 底下仍取得 package identity（VFS / AppData 重導照常）。
    /// 主程式這端負責：把封裝家族名稱解析成「目前版本」的 exe 絕對路徑，寫進 Shared.ini [Launch]，
    /// PhantomKey 讀到就啟動。由主程式當場解析，封裝 App 更新後路徑自動跟上，不會有版本殘留問題。
    ///
    /// 已驗證的關鍵事實：
    ///   1. 封裝 App 的真實 exe 位在 %ProgramFiles%\WindowsApps\&lt;PackageFullName&gt;\，
    ///      該目錄僅 TrustedInstaller / SYSTEM 可寫，一般使用者唯讀。
    ///   2. 由 CreateProcess 直接啟動該 exe，行程仍取得 package identity，即使不經 AppModel。
    /// </summary>
    public static class ElevatedLaunchService
    {
        /// <summary>封裝 App 一律安裝在此根目錄下；解析出的 exe 必須落在這裡才接受。</summary>
        private static readonly string WindowsAppsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

        /// <summary>
        /// 「僅系統管理員可寫」的受保護根目錄允許清單，結尾補分隔線供前綴比對。
        /// PhantomKey 端（ElevatedLaunch.cpp 的 IsAdminOnlyLocation）採完全相同的清單與語意，
        /// 兩邊必須一致：主程式據此決定 delegate（免 UAC）或 runas（跳 UAC），PhantomKey 再獨立
        /// 驗證一次當硬性關卡。WindowsApps 位在 Program Files 之下，封裝 App 一併涵蓋。
        /// </summary>
        private static readonly string[] AdminOnlyRoots = BuildAdminOnlyRoots();

        private static string[] BuildAdminOnlyRoots()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            return roots
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// exePath 是否落在「僅系統管理員可寫」的位置。是 → 交給提權的 PhantomKey 代打得以免 UAC；
        /// 否（如 C:\Games、LocalAppData 等使用者可寫資料夾）→ 呼叫端須改走 runas（跳 UAC），
        /// 否則等於讓提權行程去啟動使用者（含惡意程式）可竄改的檔案，構成提權後門。
        /// </summary>
        public static bool IsAdminOnlyLocation(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string full;
            try { full = Path.GetFullPath(exePath); }
            catch { return false; }
            return AdminOnlyRoots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 由套件家族名稱解析出可直接啟動的 Win32 exe 絕對路徑（目前安裝的版本）。
        /// 找不到套件、非 full-trust（沒有一般 exe，例如純 UWP）、或解析出的路徑不在
        /// WindowsApps 底下時回 null —— null 代表「這個平台走不了免 UAC 提權啟動」。
        /// </summary>
        public static string? ResolvePackagedExe(string packageFamilyName)
        {
            if (string.IsNullOrEmpty(packageFamilyName)) return null;
            try
            {
                var pm = new PackageManager();
                var package = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
                if (package == null)
                {
                    DebugLogger.Log($"[ElevatedLaunch] No package for family '{packageFamilyName}'.");
                    return null;
                }

                string installDir = package.InstalledLocation.Path;
                string? exeRel = ReadFirstFullTrustExecutable(Path.Combine(installDir, "AppxManifest.xml"));
                if (string.IsNullOrEmpty(exeRel))
                {
                    DebugLogger.Log($"[ElevatedLaunch] '{packageFamilyName}' has no full-trust executable (likely a pure UWP app).");
                    return null;
                }

                string exePath = Path.GetFullPath(Path.Combine(installDir, exeRel));

                // 安全防線：只接受 WindowsApps 底下的目標。那裡由 TrustedInstaller 保護、
                // 一般權限不可寫，因此讓提權的 PhantomKey 啟動它不會變成提權後門。
                // PhantomKey 端會再驗證一次同樣的前綴（Shared.ini 可被使用者改寫，不能只信這邊）。
                if (!exePath.StartsWith(WindowsAppsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLogger.Log($"[ElevatedLaunch] Rejected: resolved exe not under WindowsApps: {exePath}");
                    return null;
                }
                if (!File.Exists(exePath))
                {
                    DebugLogger.Log($"[ElevatedLaunch] Resolved exe missing: {exePath}");
                    return null;
                }

                return exePath;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedLaunch] ResolvePackagedExe failed for '{packageFamilyName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 從 AppxManifest.xml 讀出第一個 full-trust Application 的 Executable（相對套件根）。
        /// 純 UWP（EntryPoint 非 Windows.FullTrustApplication、或根本沒有 Executable 屬性）回 null。
        /// </summary>
        private static string? ReadFirstFullTrustExecutable(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return null;

            // manifest 的預設命名空間；Application / Executable / EntryPoint 都在這個 foundation ns 下。
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var doc = XDocument.Load(manifestPath);

            foreach (var app in doc.Descendants(ns + "Application"))
            {
                string? exe = (string?)app.Attribute("Executable");
                if (string.IsNullOrEmpty(exe)) continue;

                string? entry = (string?)app.Attribute("EntryPoint");
                // Windows.FullTrustApplication = 封裝 Win32；其餘（如 UWP 的實際型別入口）略過。
                if (!string.Equals(entry, "Windows.FullTrustApplication", StringComparison.OrdinalIgnoreCase))
                    continue;

                return exe.Replace('/', '\\');
            }
            return null;
        }

        /// <summary>
        /// 此平台策略能否走免 UAC 提權啟動：必須是帶家族名稱的 PackagedApp，且該套件解析得出
        /// full-trust exe（純 UWP 如 Xbox App 解析不到，回 false）。純檢查用途，會實際做一次解析。
        /// </summary>
        public static bool CanElevatePackaged(string? packageFamilyName) =>
            !string.IsNullOrEmpty(packageFamilyName) && ResolvePackagedExe(packageFamilyName!) != null;

        /// <summary>
        /// 委派 PhantomKey 以系統管理員身分啟動封裝 App：解析目前版本 exe → 委派。
        /// 解析失敗（純 UWP / 找不到）回 false，讓呼叫端退回一般權限啟動。
        /// </summary>
        public static bool TryLaunchElevatedPackaged(string packageFamilyName)
        {
            string? exePath = ResolvePackagedExe(packageFamilyName);
            if (exePath == null) return false;
            // 封裝 App 的 exe 位在 WindowsApps（Program Files 之下），必定通過 IsAdminOnlyLocation。
            Delegate(exePath, arguments: "");
            return true;
        }

        /// <summary>
        /// 委派 PhantomKey 以系統管理員身分啟動一般 exe（Registry / Executable 策略用）。
        /// 前提：呼叫端已確認 exe 在 <see cref="IsAdminOnlyLocation"/> 位置且支援可用；
        /// 使用者可寫位置的 exe 不該走這裡（呼叫端應改走 runas）。
        /// </summary>
        public static void LaunchElevatedExe(string exePath, string arguments) =>
            Delegate(exePath, arguments);

        /// <summary>
        /// 共用委派路徑：寫請求進 Shared.ini [Launch] → 確保提權版 PhantomKey 正在跑（它讀到就啟動）。
        ///
        /// 前提：呼叫端已確認系統管理員程式支援可用（<see cref="ElevatedInputService.IsElevatedRuntimeAvailable"/>），
        /// 否則 PhantomKey 不會是 High IL，請求不會被處理。
        /// </summary>
        private static void Delegate(string exePath, string arguments)
        {
            // 前景交接不在這裡處理。曾試過 AllowSetForegroundWindow(ASFW_ANY) + PhantomKey 主動抬
            // 視窗，但那在「開機影片與平台同時載入」（Async）模式下會搶在影片還在播時就把平台蓋到
            // 影片上（看不到影片只聽到聲音）；而且該授權有時效，長時間冷啟動後就失效。改由 LaunchPage
            // 用 PhantomKey 回報的 PID 偵測平台視窗出現後、於正確時機（影片播完）把自己藏起來，
            // 讓平台自然浮到前景——時序正確、不受計時影響，也不需要爭前景權。

            // 先寫請求再啟動 PhantomKey：這樣即使 PhantomKey 現在沒在跑，等它被叫起來後也會在
            // 啟動當下讀到這筆請求（見 PhantomKey.cpp 的 handleLaunchRequest 啟動點）。
            // 已在跑的實例則透過 Shared.ini mtime 變動偵測到。
            SettingsService.WriteElevatedLaunchRequest(exePath, arguments);
            DebugLogger.Log($"[ElevatedLaunch] Delegated to PhantomKey: {exePath} {arguments}");

            // 確保提權版 PhantomKey 正在執行（已在跑且健康則略過）。丟到背景執行緒：Start() 會做
            // ping 逾時量測、必要時還有 Kill() 的等待迴圈，別卡住啟動路徑所在的 UI 執行緒。
            // 不需要等它回來——請求已寫進 Shared.ini，PhantomKey 起來後（或已在跑的實例）自會讀到。
            _ = System.Threading.Tasks.Task.Run(() => PhantomKeyService.Start());
        }
    }
}
