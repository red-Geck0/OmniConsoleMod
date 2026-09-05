using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理 PhantomKey 背景手把輸入服務的啟動、停止與狀態查詢。
    /// PhantomKey 會自動偵測前景程式，將手把 View 按鈕映射為對應的鍵盤快速鍵。
    ///
    /// 兩種執行型態：
    ///   一般權限 — 從 %LOCALAPPDATA%\OmniConsole\Steam.exe 直接啟動（預設）。
    ///   系統管理員權限 — 已安裝系統管理員程式支援時，改由排程工作啟動
    ///     %ProgramData%\OmniConsoleMod\Steam.exe，這樣 SendInput 才送得進以系統管理員
    ///     身分執行的前景程式（見 ElevatedInputService）。
    /// </summary>
    public static class PhantomKeyService
    {
        private static readonly string _sourceExePath = Path.Combine(
            Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Steam.exe");

        private static readonly string _targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniConsole");

        private static readonly string _targetExePath = Path.Combine(_targetDir, "Steam.exe");

        /// <summary>套件內 PhantomKey 的完整路徑，供其他服務（如 AboutInfoService）讀版本號使用。</summary>
        public static string PackageExePath => _sourceExePath;

        /// <summary>使用者 LocalAppData 下實際執行的 PhantomKey 副本路徑，供 AboutInfoService 對比版本號使用。</summary>
        public static string DeployedExePath => _targetExePath;

        /// <summary>LocalAppData 部署目錄；ElevatedInputService 也用它暫存 PhantomWarden。</summary>
        public static string DeployedDir => _targetDir;

        /// <summary>
        /// 啟動 PhantomKey。
        ///
        /// 已安裝且版本相符的系統管理員程式支援 → 交給排程工作以 High IL 啟動；
        /// 否則沿用一般權限路徑：若套件內版本較新則先覆蓋 LocalAppData 的副本再啟動。
        ///
        /// 兩條路徑都會先做健康檢查：已在執行且健康（ping 通且主迴圈正在推進）就不重複啟動；
        /// 在執行但不健康（卡住、凍結、舊版無 ping window）則先終止再啟動（自癒）。
        /// </summary>
        public static void Start()
        {
            if (UseElevatedPath())
            {
                StartElevated();
                return;
            }

            if (!File.Exists(_sourceExePath))
            {
                DebugLogger.Log($"[PhantomKeyService] .exe not found in package: {_sourceExePath}");
                return;
            }

            try
            {
                Directory.CreateDirectory(_targetDir);

                // 僅在套件版本與本機版本不同時複製（MSIX 更新後自動部署新版）
                if (NeedsCopy())
                {
                    // 舊版若仍在執行中會鎖定檔案，需先終止再覆蓋
                    Kill();
                    File.Copy(_sourceExePath, _targetExePath, overwrite: true);
                    DebugLogger.Log($"[PhantomKeyService] Copied to: {_targetExePath}");
                }
                else if (IsRunning())
                {
                    // 健康檢查：透過 ping window 量測主迴圈推進狀況
                    // 健康 → 跳過；不健康（卡住/凍結/舊版無 ping window）→ 終止並重啟（自癒）
                    var health = AboutInfoService.GetPhantomKeyHealth();
                    DebugLogger.Log($"[PhantomKeyService] Ping result: responsiveness={health.Responsiveness}, lag={health.PingLagMs}ms, uptime={health.Uptime}");

                    if (IsHealthyResponsiveness(health.Responsiveness))
                    {
                        DebugLogger.Log("[PhantomKeyService] Existing instance healthy, skipping start.");
                        return;
                    }

                    DebugLogger.Log($"[PhantomKeyService] Existing instance unhealthy ({health.Responsiveness}), self-healing: kill + restart.");
                    Kill();
                }

                Process.Start(new ProcessStartInfo(_targetExePath) { UseShellExecute = true });
                DebugLogger.Log($"[PhantomKeyService] Started: {_targetExePath}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PhantomKeyService] Start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 系統管理員程式支援已安裝、且 ProgramData 那份版本與套件一致時走提權路徑。
        /// 版本不一致代表主程式更新過但支援元件還沒跟著更新（更新需要另一次 UAC，由 UI 負責徵詢），
        /// 此時先退回一般權限路徑，至少讓映射在非提權程式上照常運作。
        /// </summary>
        private static bool UseElevatedPath() =>
            ElevatedInputService.IsInstalled() && !ElevatedInputService.NeedsUpdate();

        /// <summary>
        /// 提權路徑的啟動：執行檔由 PhantomWarden 放在 ProgramData（一般權限不可寫、也不由這裡複製），
        /// 這裡只負責確保跑著的是那一份。
        /// </summary>
        private static void StartElevated()
        {
            try
            {
                string elevatedPath = ElevatedInputService.InstalledExePath;
                string? running = GetRunningInstancePath();

                if (running != null)
                {
                    if (elevatedPath.Equals(running, StringComparison.OrdinalIgnoreCase))
                    {
                        var health = AboutInfoService.GetPhantomKeyHealth();
                        DebugLogger.Log($"[PhantomKeyService] Elevated instance ping: responsiveness={health.Responsiveness}, lag={health.PingLagMs}ms");
                        if (IsHealthyResponsiveness(health.Responsiveness))
                        {
                            DebugLogger.Log("[PhantomKeyService] Elevated instance healthy, skipping start.");
                            return;
                        }
                        DebugLogger.Log($"[PhantomKeyService] Elevated instance unhealthy ({health.Responsiveness}), self-healing: stop + restart.");
                    }
                    else
                    {
                        // 一般權限那份佔著單例 mutex，提權版起不來，先請它收工
                        DebugLogger.Log($"[PhantomKeyService] Non-elevated instance running ({running}); stopping it first.");
                    }
                    Kill();
                }

                if (!ElevatedInputService.RunElevatedPhantomKey())
                    DebugLogger.Log("[PhantomKeyService] Elevated start failed; mappings will not reach administrator apps.");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PhantomKeyService] StartElevated failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 健康分級判定：Responsive / Busy 視為健康；Stuck / Hung / NoPingWindow / NotRunning 視為不健康。
        /// NoPingWindow 包含「使用者裝著不支援 ping 服務的舊版 PhantomKey」這類情境，藉此次啟動順帶完成版本汰換。
        /// </summary>
        private static bool IsHealthyResponsiveness(AboutInfoService.PhantomKeyResponsiveness r)
        {
            return r == AboutInfoService.PhantomKeyResponsiveness.Responsive
                || r == AboutInfoService.PhantomKeyResponsiveness.Busy;
        }

        /// <summary>
        /// 終止 PhantomKey（兩種部署路徑都涵蓋）。
        ///
        /// 提權版無法用 Process.Kill 終止——一般權限開不了高完整性等級行程的 handle。
        /// 因此先透過 Shared.ini 的 StopRequested 旗標請它自己收工，等它退出後再對殘存的
        /// 一般權限行程（或不認得旗標的舊版）補上 Process.Kill。
        /// </summary>
        public static void Kill()
        {
            try
            {
                // 只有提權版需要走旗標這條路（也只有它殺不掉）。一般權限那份直接 Process.Kill
                // 即可，不必為了它在呼叫端多等——Start() 的複製路徑會在 UI 執行緒上呼叫本方法。
                string? running = GetRunningInstancePath();
                if (running != null &&
                    ElevatedInputService.InstalledExePath.Equals(running, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsService.RequestPhantomKeyStop();
                    // PhantomKey 每 ~50ms 檢查一次設定檔變動，正常情況下 200ms 內就收工
                    for (int i = 0; i < 30 && IsRunning(); i++) System.Threading.Thread.Sleep(100);
                    SettingsService.ClearPhantomKeyStop();
                    DebugLogger.Log($"[PhantomKeyService] Stop flag issued to elevated instance; still running={IsRunning()}.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PhantomKeyService] Stop request failed: {ex.Message}");
            }

            foreach (var proc in Process.GetProcessesByName("Steam"))
            {
                try
                {
                    string? path = GetProcessPath(proc.Id);
                    if (path != null && IsKnownPhantomKeyPath(path))
                    {
                        DebugLogger.Log($"[PhantomKeyService] Killing PID={proc.Id} Path={path}");
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[PhantomKeyService] Kill failed for PID={proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        /// <summary>
        /// 比對套件內與使用者目錄的 .exe，判斷是否需要複製。
        /// </summary>
        private static bool NeedsCopy()
        {
            if (!File.Exists(_targetExePath))
            {
                DebugLogger.Log("[PhantomKeyService] NeedsCopy: target not found, need copy.");
                return true;
            }

            var sourceVer = FileVersionInfo.GetVersionInfo(_sourceExePath).FileVersion;
            var targetVer = FileVersionInfo.GetVersionInfo(_targetExePath).FileVersion;
            bool needsCopy = sourceVer != targetVer;
            DebugLogger.Log($"[PhantomKeyService] NeedsCopy: source={sourceVer}, target={targetVer}, needsCopy={needsCopy}");
            return needsCopy;
        }

        /// <summary>是否為我們認得的 PhantomKey 部署路徑（一般權限或提權）。</summary>
        private static bool IsKnownPhantomKeyPath(string path) =>
            _targetExePath.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            ElevatedInputService.InstalledExePath.Equals(path, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 找出正在執行的 PhantomKey 並回傳其完整路徑；沒有則回 null。
        ///
        /// 刻意不用 Process.MainModule — 它需要 PROCESS_QUERY_INFORMATION | VM_READ，
        /// 一般權限對提權行程會被拒。QueryFullProcessImageName 只需
        /// PROCESS_QUERY_LIMITED_INFORMATION，同一使用者的提權行程也查得到。
        /// </summary>
        public static string? GetRunningInstancePath()
        {
            foreach (var proc in Process.GetProcessesByName("Steam"))
            {
                try
                {
                    string? path = GetProcessPath(proc.Id);
                    if (path != null && IsKnownPhantomKeyPath(path)) return path;
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
            return null;
        }

        /// <summary>
        /// 檢查是否有 PhantomKey 正在執行（一般權限或提權皆算）。
        /// </summary>
        public static bool IsRunning() => GetRunningInstancePath() != null;

        // ── 行程路徑查詢（跨完整性等級） ─────────────────────────────────────

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr process, uint flags, StringBuilder buffer, ref uint size);

        /// <summary>取行程的完整執行檔路徑；取不到回 null。</summary>
        internal static string? GetProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                    ? buffer.ToString()
                    : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
