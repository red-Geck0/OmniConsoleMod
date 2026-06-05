// PhantomMute.dll
// Di-inject ke proses game via SetWindowsHookEx(WH_GETMESSAGE). Tugasnya:
//   1. Resolve alamat asli XInputGetState (+ XInputGetStateEx ordinal 100) dari
//      modul xinput yang ter-load di game.
//   2. IAT-hook semua modul game yang meng-import fungsi tsb → arahkan ke detour kita.
//   3. Detour membaca flag shared-memory (diisi PhantomKey). Saat active && targetPid
//      cocok dengan PID proses ini → kembalikan state KOSONG (stick center, no button)
//      sehingga game tidak melihat input gamepad (mencegah double-input). Saat tidak
//      active → pass-through ke fungsi asli.
//
// Hanya IAT hook (tanpa dependency eksternal): mengcover game yang meng-import XInput
// secara statis (mayoritas). Game yang resolve via GetProcAddress dinamis belum tercover
// (iterasi berikutnya bisa pakai inline hook bila perlu).

#include <windows.h>
#include <xinput.h>
#include <tlhelp32.h>
#include <string.h>
#include "PhantomMuteShared.h"

// ---- XInput typedefs ----
typedef DWORD(WINAPI* PfnXInputGetState)(DWORD, XINPUT_STATE*);

// ---- State global DLL ----
static PhantomMuteState* g_state = nullptr;     // view shared-memory (read-only logis)
static HANDLE            g_mapping = nullptr;
static PfnXInputGetState g_realGetState = nullptr;   // export asli XInputGetState
static PfnXInputGetState g_realGetStateEx = nullptr; // export asli XInputGetStateEx (ord 100)
static LONG              g_fakePacket = 0;
static HANDLE            g_rescanThread = nullptr;
static volatile LONG     g_stop = 0;
static DWORD             g_selfPid = 0;

// Daftar nama modul xinput yang mungkin dipakai game (urut prioritas).
static const wchar_t* kXInputModules[] = {
    L"xinput1_4.dll", L"xinput1_3.dll", L"xinput9_1_0.dll",
    L"xinput1_2.dll", L"xinput1_1.dll",
};

// Apakah mute aktif untuk proses ini sekarang.
static inline bool MuteActive() {
    return g_state && g_state->active && g_state->targetPid == g_selfPid;
}

// Isi state kosong (controller tetap "connected" tapi idle).
static inline void FillEmpty(XINPUT_STATE* p) {
    if (!p) return;
    ZeroMemory(p, sizeof(*p));
    p->dwPacketNumber = (DWORD)InterlockedIncrement(&g_fakePacket);
}

// ---- Detour ----
static DWORD WINAPI Hook_XInputGetState(DWORD idx, XINPUT_STATE* pState) {
    if (MuteActive()) { FillEmpty(pState); return ERROR_SUCCESS; }
    if (g_realGetState) return g_realGetState(idx, pState);
    return ERROR_DEVICE_NOT_CONNECTED;
}
static DWORD WINAPI Hook_XInputGetStateEx(DWORD idx, XINPUT_STATE* pState) {
    if (MuteActive()) { FillEmpty(pState); return ERROR_SUCCESS; }
    if (g_realGetStateEx) return g_realGetStateEx(idx, pState);
    return ERROR_DEVICE_NOT_CONNECTED;
}

// Resolve alamat fungsi asli dari modul xinput yang sudah ter-load di proses.
static void EnsureOriginals() {
    if (g_realGetState && g_realGetStateEx) return;
    for (const wchar_t* name : kXInputModules) {
        HMODULE h = GetModuleHandleW(name);
        if (!h) continue;
        if (!g_realGetState) {
            auto p = reinterpret_cast<PfnXInputGetState>(GetProcAddress(h, "XInputGetState"));
            if (p) g_realGetState = p;
        }
        if (!g_realGetStateEx) {
            // XInputGetStateEx hanya diekspor lewat ordinal 100 (tanpa nama).
            auto p = reinterpret_cast<PfnXInputGetState>(GetProcAddress(h, MAKEINTRESOURCEA(100)));
            if (p) g_realGetStateEx = p;
        }
        if (g_realGetState && g_realGetStateEx) break;
    }
}

// Timpa satu entri IAT (thunk) dengan alamat detour.
static void PatchThunk(PIMAGE_THUNK_DATA thunk, void* detour) {
    DWORD old = 0;
    if (VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) {
        thunk->u1.Function = reinterpret_cast<ULONGLONG>(detour);
        VirtualProtect(&thunk->u1.Function, sizeof(void*), old, &old);
    }
}

// Patch IAT satu modul: cari import dari xinput*.dll bernama "XInputGetState"
// atau ber-ordinal 100, arahkan ke detour.
static void PatchModuleIAT(HMODULE hMod) {
    auto base = reinterpret_cast<BYTE*>(hMod);
    auto dos = reinterpret_cast<PIMAGE_DOS_HEADER>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    auto nt = reinterpret_cast<PIMAGE_NT_HEADERS>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;

    DWORD impRva = nt->OptionalHeader
        .DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!impRva) return;

    auto imp = reinterpret_cast<PIMAGE_IMPORT_DESCRIPTOR>(base + impRva);
    for (; imp->Name; ++imp) {
        const char* dllName = reinterpret_cast<const char*>(base + imp->Name);
        // hanya pedulikan import dari xinput*
        if (_strnicmp(dllName, "xinput", 6) != 0) continue;

        auto orig = reinterpret_cast<PIMAGE_THUNK_DATA>(
            base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto iat = reinterpret_cast<PIMAGE_THUNK_DATA>(base + imp->FirstThunk);

        for (; orig->u1.AddressOfData; ++orig, ++iat) {
            if (orig->u1.Ordinal & IMAGE_ORDINAL_FLAG) {
                // import by ordinal → 100 = XInputGetStateEx
                if (IMAGE_ORDINAL(orig->u1.Ordinal) == 100 && g_realGetStateEx)
                    PatchThunk(iat, reinterpret_cast<void*>(&Hook_XInputGetStateEx));
            } else {
                auto byName = reinterpret_cast<PIMAGE_IMPORT_BY_NAME>(base + orig->u1.AddressOfData);
                if (g_realGetState && strcmp(byName->Name, "XInputGetState") == 0)
                    PatchThunk(iat, reinterpret_cast<void*>(&Hook_XInputGetState));
            }
        }
    }
}

// Patch semua modul yang sedang ter-load.
static void PatchAllModules() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    MODULEENTRY32W me = { sizeof(me) };
    if (Module32FirstW(snap, &me)) {
        do {
            PatchModuleIAT(me.hModule);
        } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
}

// Thread ringan: resolve original + re-patch IAT berkala agar modul yang ter-load
// belakangan (engine plugin, dsb.) ikut ter-hook.
static DWORD WINAPI RescanThread(LPVOID) {
    for (int i = 0; i < 40 && !g_stop; ++i) { // ~10 detik awal, agresif
        EnsureOriginals();
        if (g_realGetState || g_realGetStateEx) PatchAllModules();
        Sleep(250);
    }
    while (!g_stop) { // steady state
        EnsureOriginals();
        if (g_realGetState || g_realGetStateEx) PatchAllModules();
        Sleep(1000);
    }
    return 0;
}

static void OpenSharedState() {
    g_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, PHANTOMMUTE_MAPPING_NAME);
    if (g_mapping)
        g_state = reinterpret_cast<PhantomMuteState*>(
            MapViewOfFile(g_mapping, FILE_MAP_READ, 0, 0, sizeof(PhantomMuteState)));
}

// Export untuk SetWindowsHookEx — isinya trivial; tujuannya hanya memuat DLL ini
// ke proses target. Logika hook sebenarnya berjalan di DllMain attach + rescan thread.
extern "C" __declspec(dllexport)
LRESULT CALLBACK PhantomMuteGetMsgProc(int code, WPARAM wParam, LPARAM lParam) {
    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        g_selfPid = GetCurrentProcessId();
        OpenSharedState();
        EnsureOriginals();
        if (g_realGetState || g_realGetStateEx) PatchAllModules();
        g_rescanThread = CreateThread(nullptr, 0, RescanThread, nullptr, 0, nullptr);
        break;
    case DLL_PROCESS_DETACH:
        InterlockedExchange(&g_stop, 1);
        // tidak join thread agar unload cepat & aman; proses umumnya exit setelah ini
        if (g_state)   UnmapViewOfFile(g_state);
        if (g_mapping) CloseHandle(g_mapping);
        break;
    }
    return TRUE;
}
