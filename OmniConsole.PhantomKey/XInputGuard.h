// XInputGuard.h
// Sisi PhantomKey: pemilik shared-memory flag + manajer injeksi PhantomMute.dll.
// Dipanggil dari main loop tiap tick. Saat layer mapping aktif untuk game foreground,
// DLL di-inject (sekali) ke proses game via SetWindowsHookEx, lalu mute di-toggle live
// lewat flag shared-memory (tanpa restart game).
#pragma once
#include <windows.h>

namespace XInputGuard {

// Siapkan shared-memory + deploy PhantomMute.dll ke lokasi yang bisa dibaca proses game.
// Aman dipanggil sekali saat startup; bila gagal (DLL tak ada, dsb.) Update() menjadi no-op.
void Init();

// Dipanggil tiap tick.
//   active  : true bila layer mapping (Mouse Mode) sedang aktif untuk foreground.
//   fgHwnd  : HWND foreground saat ini (sumber PID/TID target).
// Mengelola hook + menyetel flag mute sesuai kondisi.
void Update(bool active, HWND fgHwnd);

// Lepas hook + bersihkan. Dipanggil saat PhantomKey keluar.
void Shutdown();

} // namespace XInputGuard
