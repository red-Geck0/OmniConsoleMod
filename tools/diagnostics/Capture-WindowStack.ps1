<#
.SYNOPSIS
  Merekam tumpukan jendela (z-order) secara berkala untuk melacak layar hitam.

.DESCRIPTION
  Tiap beberapa detik mencatat jendela foreground dan jendela top-level yang
  terlihat, dari paling atas ke bawah, beserta:
    - nama proses, PID, dan apakah prosesnya jalan sebagai admin
    - class, judul, posisi/ukuran
    - status cloak DWM (0 = tampil; 1 = disembunyikan app; 2 = disembunyikan shell;
      4 = ikut induknya) -- jendela yang di-cloak tetap dianggap "visible" oleh Win32
      tapi tidak digambar sama sekali
    - band z-order (1 = desktop biasa; angka lain = lapisan sistem seperti Game Bar)
    - flag TOPMOST / TOOL / LAYERED / TRANSP / NOACT

  Snapshot yang sama persis dengan sebelumnya hanya ditulis "(unchanged)".

.EXAMPLE
  powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File .\Capture-WindowStack.ps1

  Jalankan sebelum mulai tes, reproduksi layar hitam, biarkan hitam ~10 detik,
  lalu pulihkan seperti biasa. Log ada di Desktop (WindowStack_*.log).
  Jalankan dari PowerShell admin agar kolom admin/user terisi untuk semua proses.
#>
param(
    [double]$IntervalSeconds = 2,
    [double]$DurationMinutes = 20,
    [int]$MaxWindows = 25,
    [string]$OutFile = (Join-Path ([Environment]::GetFolderPath('Desktop')) ("WindowStack_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date)))
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class WinStack
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowBand(IntPtr hWnd, out uint band);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder sb, ref uint size);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

    const uint GW_HWNDNEXT = 2;
    const int GWL_EXSTYLE = -20;
    const int DWMWA_CLOAKED = 14;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint TOKEN_QUERY = 0x8;
    const int TokenElevation = 20;

    static bool bandUnavailable;

    public static void Init()
    {
        // Koordinat asli monitor, bukan hasil virtualisasi DPI
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    public static string[] Snapshot(int max)
    {
        var lines = new List<string>();
        var procs = new Dictionary<uint, string>();
        IntPtr fg = GetForegroundWindow();
        lines.Add("FG  " + Describe(fg, procs));

        int n = 0;
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && n < max; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h) || IsIconic(h)) continue;
            RECT r;
            if (!GetWindowRect(h, out r) || r.R - r.L <= 0 || r.B - r.T <= 0) continue;
            n++;
            lines.Add(string.Format("{0,2}{1} {2}", n, h == fg ? "*" : " ", Describe(h, procs)));
        }
        return lines.ToArray();
    }

    static string Describe(IntPtr h, Dictionary<uint, string> procs)
    {
        if (h == IntPtr.Zero) return "(none)";

        uint pid;
        GetWindowThreadProcessId(h, out pid);
        string proc;
        if (!procs.TryGetValue(pid, out proc))
        {
            proc = ProcessInfo(pid);
            procs[pid] = proc;
        }

        var cls = new StringBuilder(128);
        GetClassNameW(h, cls, cls.Capacity);
        var title = new StringBuilder(256);
        GetWindowTextW(h, title, title.Capacity);
        string t = title.ToString();
        if (t.Length > 40) t = t.Substring(0, 40) + "...";

        RECT r;
        GetWindowRect(h, out r);
        int cloak = 0;
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloak, 4);

        int ex = GetWindowLongW(h, GWL_EXSTYLE);
        var flags = new List<string>();
        if ((ex & 0x00000008) != 0) flags.Add("TOPMOST");
        if ((ex & 0x00000080) != 0) flags.Add("TOOL");
        if ((ex & 0x00080000) != 0) flags.Add("LAYERED");
        if ((ex & 0x00000020) != 0) flags.Add("TRANSP");
        if ((ex & 0x08000000) != 0) flags.Add("NOACT");

        return string.Format("0x{0:X} {1} cls={2} \"{3}\" rect=({4},{5})-({6},{7}) cloak={8} band={9} {10}",
            h.ToInt64(), proc, cls, t, r.L, r.T, r.R, r.B, cloak, Band(h), string.Join(",", flags.ToArray()));
    }

    static string ProcessInfo(uint pid)
    {
        IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return "pid=" + pid + "(no access)";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            string name = QueryFullProcessImageNameW(hp, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : "?";

            // Proses admin biasanya menolak dibaca token-nya dari PowerShell biasa -> "?"
            string elevation = "?";
            IntPtr token;
            if (OpenProcessToken(hp, TOKEN_QUERY, out token))
            {
                int elevated, len;
                if (GetTokenInformation(token, TokenElevation, out elevated, 4, out len))
                    elevation = elevated != 0 ? "admin" : "user";
                CloseHandle(token);
            }
            return name + "(pid=" + pid + "," + elevation + ")";
        }
        finally
        {
            CloseHandle(hp);
        }
    }

    static string Band(IntPtr h)
    {
        if (bandUnavailable) return "?";
        try
        {
            uint band;
            return GetWindowBand(h, out band) ? band.ToString() : "?";
        }
        catch (EntryPointNotFoundException)
        {
            bandUnavailable = true;
            return "?";
        }
    }
}
'@

[WinStack]::Init()

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$header = "Capture-WindowStack start {0:yyyy-MM-dd HH:mm:ss}  os={1}  scriptAsAdmin={2}  interval={3}s  duration={4}min" -f `
    (Get-Date), [Environment]::OSVersion.Version, $isAdmin, $IntervalSeconds, $DurationMinutes
Add-Content -Path $OutFile -Value $header -Encoding UTF8

$end = (Get-Date).AddMinutes($DurationMinutes)
$previous = $null
while ((Get-Date) -lt $end) {
    $stamp = Get-Date -Format 'HH:mm:ss.fff'
    try {
        $body = [WinStack]::Snapshot($MaxWindows) -join "`r`n"
        if ($body -ceq $previous) {
            Add-Content -Path $OutFile -Value "[$stamp] (unchanged)" -Encoding UTF8
        } else {
            Add-Content -Path $OutFile -Value "[$stamp]`r`n$body`r`n" -Encoding UTF8
            $previous = $body
        }
    } catch {
        Add-Content -Path $OutFile -Value "[$stamp] snapshot failed: $($_.Exception.Message)" -Encoding UTF8
    }
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))
}
Add-Content -Path $OutFile -Value ("Capture-WindowStack end {0:HH:mm:ss}" -f (Get-Date)) -Encoding UTF8
