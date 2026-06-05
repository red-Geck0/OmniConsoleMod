// XInputGuard.cpp  — lihat XInputGuard.h
#include "XInputGuard.h"
#include "Log.h"
#include "../OmniConsole.PhantomMute/PhantomMuteShared.h"
#include <shlobj.h>
#include <string>

#pragma comment(lib, "shell32.lib")

namespace {

HANDLE            g_mapping = nullptr;
PhantomMuteState* g_state = nullptr;
HMODULE           g_muteDll = nullptr;          // handle DLL di proses PhantomKey (untuk hMod hook)
HOOKPROC          g_hookProc = nullptr;
HHOOK             g_hook = nullptr;
DWORD             g_hookedTid = 0;
DWORD             g_hookedPid = 0;
bool              g_ready = false;

// Forward decl (definisi di bawah).
bool PathFileExistsW_(const std::wstring& p);

// Buat/buka shared-memory dengan DACL longgar agar proses game (user sama) bisa membaca.
bool CreateSharedState() {
    // Default security sudah mengizinkan akses bagi token user yang sama; cukup untuk
    // game desktop yang berjalan sebagai user yang sama dengan PhantomKey.
    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                   0, sizeof(PhantomMuteState), PHANTOMMUTE_MAPPING_NAME);
    if (!g_mapping) {
        Log(L"[XInputGuard] CreateFileMapping failed (%lu).", GetLastError());
        return false;
    }
    g_state = reinterpret_cast<PhantomMuteState*>(
        MapViewOfFile(g_mapping, FILE_MAP_WRITE, 0, 0, sizeof(PhantomMuteState)));
    if (!g_state) {
        Log(L"[XInputGuard] MapViewOfFile failed (%lu).", GetLastError());
        return false;
    }
    g_state->version = 0;
    g_state->targetPid = 0;
    g_state->active = 0;
    return true;
}

// Folder LocalCache milik PhantomKey (punya package identity → CSIDL_LOCAL_APPDATA
// ter-redirect ke ...\Packages\<fam>\LocalCache\Local). Proses game (user sama) bisa
// membaca path absolut ini, sedangkan path di WindowsApps biasanya ditolak ACL.
std::wstring DeployDir() {
    wchar_t local[MAX_PATH];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, local)))
        return L"";
    std::wstring dir = std::wstring(local) + L"\\OmniConsole";
    CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

// Path sumber DLL = folder Steam.exe (di dalam package) + nama DLL.
std::wstring SourceDllPath() {
    wchar_t self[MAX_PATH];
    if (!GetModuleFileNameW(nullptr, self, MAX_PATH)) return L"";
    std::wstring p(self);
    size_t slash = p.find_last_of(L'\\');
    if (slash == std::wstring::npos) return L"";
    return p.substr(0, slash + 1) + L"OmniConsole.PhantomMute.dll";
}

// Salin DLL ke folder yang bisa diakses game, lalu LoadLibrary dari sana.
bool DeployAndLoadDll() {
    std::wstring src = SourceDllPath();
    std::wstring dir = DeployDir();
    if (src.empty() || dir.empty()) return false;
    std::wstring dst = dir + L"\\OmniConsole.PhantomMute.dll";

    // Timpa salinan lama (CopyFile gagal bila DLL sedang di-load proses lain; abaikan,
    // pakai salinan yang ada). FALSE = overwrite bila memungkinkan.
    if (!CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
        DWORD e = GetLastError();
        if (!PathFileExistsW_(dst)) {
            Log(L"[XInputGuard] CopyFile failed (%lu) and no existing copy.", e);
            return false;
        }
        Log(L"[XInputGuard] CopyFile failed (%lu); using existing copy.", e);
    }

    g_muteDll = LoadLibraryW(dst.c_str());
    if (!g_muteDll) {
        Log(L"[XInputGuard] LoadLibrary('%s') failed (%lu).", dst.c_str(), GetLastError());
        return false;
    }
    g_hookProc = reinterpret_cast<HOOKPROC>(GetProcAddress(g_muteDll, PHANTOMMUTE_HOOKPROC_NAME));
    if (!g_hookProc) {
        Log(L"[XInputGuard] GetProcAddress('%S') failed.", PHANTOMMUTE_HOOKPROC_NAME);
        return false;
    }
    return true;
}

// PathFileExists tanpa menarik shlwapi: cek atribut file.
bool PathFileExistsW_(const std::wstring& p) {
    return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES;
}

void Unhook() {
    if (g_hook) {
        UnhookWindowsHookEx(g_hook);
        g_hook = nullptr;
    }
    g_hookedTid = 0;
    g_hookedPid = 0;
}

// Pasang hook WH_GETMESSAGE pada thread game → Windows memuat DLL ke prosesnya.
bool HookThread(DWORD tid, DWORD pid) {
    HHOOK h = SetWindowsHookExW(WH_GETMESSAGE, g_hookProc, g_muteDll, tid);
    if (!h) {
        Log(L"[XInputGuard] SetWindowsHookEx(tid=%lu) failed (%lu).", tid, GetLastError());
        return false;
    }
    g_hook = h;
    g_hookedTid = tid;
    g_hookedPid = pid;
    // Paksa thread memproses sebuah message agar DLL ter-load segera.
    PostThreadMessageW(tid, WM_NULL, 0, 0);
    Log(L"[XInputGuard] Injected into pid=%lu tid=%lu.", pid, tid);
    return true;
}

} // namespace

namespace XInputGuard {

void Init() {
    if (!CreateSharedState()) return;
    if (!DeployAndLoadDll()) return;
    g_ready = true;
    Log(L"[XInputGuard] Ready.");
}

void Update(bool active, HWND fgHwnd) {
    if (!g_ready) return;

    DWORD pid = 0;
    DWORD tid = fgHwnd ? GetWindowThreadProcessId(fgHwnd, &pid) : 0;

    if (active && tid && pid) {
        // Target berganti game → re-inject ke thread baru (yang lama auto-unload).
        if (g_hookedTid != tid) {
            Unhook();
            HookThread(tid, pid);
        }
        g_state->targetPid = pid;
        InterlockedExchange(&g_state->active, 1);
        InterlockedIncrement(&g_state->version);
    } else {
        // Tidak aktif → matikan mute.
        if (g_state->active) {
            InterlockedExchange(&g_state->active, 0);
            InterlockedIncrement(&g_state->version);
        }
        // Foreground sudah pindah dari proses yang di-hook → lepas hook agar DLL
        // ter-unload dari game. Bila masih di game yang sama (sekadar layer mati
        // sesaat), pertahankan hook untuk hindari churn inject/unload.
        if (g_hook && pid != g_hookedPid) {
            Unhook();
        }
    }
}

void Shutdown() {
    Unhook();
    if (g_state) {
        InterlockedExchange(&g_state->active, 0);
        UnmapViewOfFile(g_state);
        g_state = nullptr;
    }
    if (g_mapping) { CloseHandle(g_mapping); g_mapping = nullptr; }
    if (g_muteDll) { FreeLibrary(g_muteDll); g_muteDll = nullptr; }
    g_ready = false;
}

} // namespace XInputGuard
