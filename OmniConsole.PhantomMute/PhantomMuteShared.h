// PhantomMuteShared.h
// Kontrak shared-memory antara PhantomKey (penulis) dan PhantomMute.dll (pembaca,
// di-inject ke proses game). PhantomKey menyetel flag tiap tick; hook XInputGetState
// di dalam game membaca flag ini setiap frame sehingga mute bisa di-toggle live
// tanpa restart game.
#pragma once
#include <windows.h>

// Named file mapping di namespace per-session (Local\). PhantomKey & game berada di
// session yang sama. Suffix versi agar bisa diubah layout tanpa bentrok versi lama.
#define PHANTOMMUTE_MAPPING_NAME L"Local\\OmniConsole_PhantomMute_v1"

// Nama fungsi hook yang diekspor DLL untuk SetWindowsHookEx (WH_GETMESSAGE).
#define PHANTOMMUTE_HOOKPROC_NAME "PhantomMuteGetMsgProc"

#pragma pack(push, 4)
struct PhantomMuteState {
    volatile LONG  version;   // dinaikkan penulis tiap perubahan (debug/observasi)
    volatile DWORD targetPid; // PID game yang XInput-nya harus di-mute
    volatile LONG  active;    // 1 = mute targetPid, 0 = pass-through
};
#pragma pack(pop)
