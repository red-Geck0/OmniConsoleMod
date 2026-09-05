using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OmniConsole.Services
{
    /// <summary>
    /// 系統管理員程式支援（Administrator App Support）的安裝狀態管理。
    ///
    /// 背景：PhantomKey 的映射靠 SendInput 送出，而 UIPI 不允許低完整性等級的行程把輸入
    /// 送進高完整性等級的視窗。前景是以系統管理員身分執行的程式時，SendInput 會被靜默
    /// 丟棄（回傳成功、也不設 last error），整組映射失效。唯一解法是讓 PhantomKey 自己
    /// 也跑在 High IL。
    ///
    /// 做法：由 PhantomWarden（資訊清單標 requireAdministrator）註冊一個「以最高權限執行」
    /// 的排程工作指向 PhantomKey。安裝時跳一次 UAC，之後主程式以一般權限叫工作跑起來即可，
    /// 不會再有提示。
    ///
    /// 排程工作指向的執行檔放在 %ProgramData%\OmniConsoleMod\，由 PhantomWarden 設成
    /// 一般權限唯讀。指向使用者可寫的目錄等於開後門——任何一般權限的程式改寫該檔案再叫
    /// 工作跑起來就直接取得系統管理員權限。
    /// </summary>
    public static class ElevatedInputService
    {
        // ── 與 OmniConsole.PhantomWarden\WardenShared.h 對應，改動時兩邊必須同步 ──
        private const string InstallFolderName = "OmniConsoleMod";
        private const string PayloadExeName = "Steam.exe";
        private const string TaskPath = @"\OmniConsoleMod\PhantomKeyElevated";

        private const string WardenExeName = "OmniConsole.PhantomWarden.exe";

        // 中繼副本的暫存資料夾（%ProgramData% 底下，用完即刪）
        private const string StagingFolderName = "OmniConsoleMod-setup";

        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            InstallFolderName);

        /// <summary>排程工作實際執行的 PhantomKey 路徑（%ProgramData%，一般權限唯讀）。</summary>
        public static string InstalledExePath => Path.Combine(InstallDir, PayloadExeName);

        private static string PackagePath =>
            Windows.ApplicationModel.Package.Current.InstalledLocation.Path;

        private static string WardenPackagePath => Path.Combine(PackagePath, WardenExeName);

        private static string SchTasksPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

        /// <summary>
        /// 是否已安裝。ProgramData 那份執行檔是 PhantomWarden 註冊工作成功後才留下的
        /// （註冊失敗會回滾刪除），因此它的存在等同「工作已註冊」，不必每次去問工作排程器。
        /// </summary>
        public static bool IsInstalled() => File.Exists(InstalledExePath);

        /// <summary>
        /// 已安裝、但 ProgramData 那份 PhantomKey 版本與套件內不一致。
        /// 一般權限改不動那個目錄，必須再跑一次 PhantomWarden（再跳一次 UAC）才能更新。
        /// </summary>
        public static bool NeedsUpdate()
        {
            if (!IsInstalled()) return false;
            try
            {
                string packageVer = FileVersionInfo.GetVersionInfo(
                    PhantomKeyService.PackageExePath).FileVersion ?? string.Empty;
                string installedVer = FileVersionInfo.GetVersionInfo(
                    InstalledExePath).FileVersion ?? string.Empty;
                return packageVer != installedVer;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] NeedsUpdate check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 目前登入的帳戶能否提權。註冊工作需要系統管理員權限，一般標準帳戶即使借用
        /// 別人的系統管理員憑證通過 UAC，工作本身仍以標準帳戶執行、拿不到 High IL，
        /// 功能等於無效——所以在跳 UAC 之前就先擋下來。
        /// </summary>
        public static bool CanUserElevate()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();

                // 已經提權，或 UAC 關閉的系統管理員 → token 本身就帶著完整權限
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    return true;

                // 未提權的系統管理員拿到的是「受限 token」（TokenElevationTypeLimited），
                // 系統另外保留一個連結的完整 token，通過 UAC 就會換到那一個 → 可以提權。
                //
                // 不可改用 WindowsIdentity.Groups 判斷：.NET 只收下屬性恰為 SE_GROUP_ENABLED 的
                // 項目，而受限 token 裡的 Administrators 是 SE_GROUP_USE_FOR_DENY_ONLY，會被整個
                // 濾掉——結果是每個未提權的系統管理員都被誤判成沒有權限的一般帳戶。
                if (GetTokenInformation(identity.AccessToken.DangerousGetHandle(),
                                        TokenElevationTypeClass, out uint type, sizeof(uint), out _))
                    return type == TokenElevationTypeLimited;

                DebugLogger.Log($"[ElevatedInputService] GetTokenInformation(TokenElevationType) failed: {Marshal.GetLastWin32Error()}");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] CanUserElevate failed: {ex.Message}");
                return false;
            }
        }

        // ── Token 提權型別查詢 ───────────────────────────────────────────────

        private const int TokenElevationTypeClass = 18;   // TOKEN_INFORMATION_CLASS::TokenElevationType
        private const uint TokenElevationTypeLimited = 3; // 未提權的系統管理員（有連結的完整 token）

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass,
            out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

        /// <summary>
        /// 安裝或更新。會跳一次 UAC；使用者按取消時回 Cancelled（不是錯誤）。
        /// 呼叫端應放在背景執行緒——UAC 期間走安全桌面，會把 UI 執行緒卡住。
        /// </summary>
        public static ElevationResult Install()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                string? sid = identity.User?.Value;
                if (string.IsNullOrEmpty(sid))
                {
                    DebugLogger.Log("[ElevatedInputService] Install aborted: current user SID unavailable.");
                    return ElevationResult.Failed;
                }

                // PhantomWarden 先讀套件內的 PhantomKey 正本；WindowsApps 讀不到時退回
                // 主程式部署在 LocalAppData 的那份副本。
                string args =
                    $"--install --sid {sid} " +
                    $"--source \"{PhantomKeyService.PackageExePath}\" " +
                    $"--source2 \"{PhantomKeyService.DeployedExePath}\"";

                return RunWardenElevated(args);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] Install failed: {ex.Message}");
                return ElevationResult.Failed;
            }
        }

        /// <summary>移除排程工作與 ProgramData 目錄。同樣會跳一次 UAC。</summary>
        public static ElevationResult Uninstall() => RunWardenElevated("--uninstall");

        /// <summary>提權動作的結果。取消 UAC 不是錯誤，不該跳錯誤訊息。</summary>
        public enum ElevationResult
        {
            Success,
            Cancelled,
            Failed,
        }

        /// <summary>
        /// 以 runas 啟動 PhantomWarden 並等它做完。
        ///
        /// 先直接從套件目錄啟動：不留中繼檔，也就沒有「複製完到啟動前被掉包」的空窗。
        /// 失敗才退回中繼副本。
        /// </summary>
        private static ElevationResult RunWardenElevated(string args)
        {
            if (!File.Exists(WardenPackagePath))
            {
                DebugLogger.Log($"[ElevatedInputService] Warden not found in package: {WardenPackagePath}");
                return ElevationResult.Failed;
            }

            var result = TryLaunchWarden(WardenPackagePath, args, "package");
            if (result != ElevationResult.Failed) return result;

            // 中繼副本刻意放 ProgramData，不放 LocalAppData：
            // MSIX 容器會把套件內對 %LOCALAPPDATA% 的寫入重導到
            // Packages\<PFN>\LocalCache\Local\...，而 runas 是交給容器外的 AppInfo 服務
            // 去建立行程，它看的是真實路徑——檔案在那裡並不存在，於是 ERROR_PATH_NOT_FOUND。
            // ProgramData 不在重導範圍內，容器內外看到的是同一個檔案。
            string stagingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StagingFolderName);
            string staged = Path.Combine(stagingDir, WardenExeName);

            try
            {
                Directory.CreateDirectory(stagingDir);
                File.Copy(WardenPackagePath, staged, overwrite: true);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] Staging copy to '{staged}' failed: {ex.Message}");
                return ElevationResult.Failed;
            }

            try
            {
                return TryLaunchWarden(staged, args, "staged");
            }
            finally
            {
                try { File.Delete(staged); } catch { }
                try { Directory.Delete(stagingDir); } catch { }
            }
        }

        /// <summary>單次 runas 啟動嘗試；label 只是給日誌用，說明用的是哪一份執行檔。</summary>
        private static ElevationResult TryLaunchWarden(string exePath, string args, string label)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    // 明確指定一個必定存在且容器內外一致的工作目錄。
                    // 不指定時 .NET 會沿用行程目前的工作目錄，在封裝環境下不見得解析得到。
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                });
                if (proc == null)
                {
                    DebugLogger.Log($"[ElevatedInputService] Warden ({label}) did not start.");
                    return ElevationResult.Failed;
                }

                proc.WaitForExit();
                DebugLogger.Log($"[ElevatedInputService] Warden ({label}) exited with code {proc.ExitCode} (args: {args}).");
                return proc.ExitCode == 0 ? ElevationResult.Success : ElevationResult.Failed;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — 使用者在 UAC 對話方塊按了取消，不是錯誤，也不必再退回中繼副本
                DebugLogger.Log($"[ElevatedInputService] UAC prompt cancelled by user ({label}).");
                return ElevationResult.Cancelled;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] Warden ({label}) launch failed: {ex.Message}");
                return ElevationResult.Failed;
            }
        }

        /// <summary>
        /// 叫排程工作把提權版 PhantomKey 跑起來。工作已註冊、且授權給目前使用者讀取與執行，
        /// 因此一般權限就叫得動，不會再跳 UAC。
        /// </summary>
        public static bool RunElevatedPhantomKey()
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(SchTasksPath, $"/Run /TN \"{TaskPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc == null) return false;

                proc.WaitForExit(10000);
                bool ok = proc.HasExited && proc.ExitCode == 0;
                DebugLogger.Log($"[ElevatedInputService] Task run requested, ok={ok}.");
                return ok;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ElevatedInputService] RunElevatedPhantomKey failed: {ex.Message}");
                return false;
            }
        }
    }
}
