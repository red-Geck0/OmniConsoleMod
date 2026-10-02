# Rebranding Notes

Catatan semua titik yang menentukan **nama produk** dan **author** yang tampil ke
pengguna. Ubah di sini kalau mau ganti nama / author. Nama saat ini:

- Nama produk: **OmniConsoleMod**
- Author / publisher display: **red-Geck0**
- Repo update-check: **red-Geck0/OmniConsoleMod**

> PENTING — JANGAN diubah saat rebranding (kalau diubah, update in-place rusak &
> setting pengguna hilang):
> - `<Identity Name=...>` di kedua `Package.appxmanifest`
> - `Publisher="CN=red-Geck0"` di kedua manifest (harus sama dengan subject
>   sertifikat penandatangan)
> - Sertifikat penandatangan (`.cer` / `.pfx`, `CN=red-Geck0`)
> - JSON `"shell": "OmniConsole"` (ini fungsional, bukan teks tampilan)

### Identitas paket (setelah pisah dari upstream)

Fork ini memakai identitas sendiri, lepas penuh dari upstream `8bit2qubit`.
Nilai konkret (menentukan update in-place & path folder shared setting):

| Item | Nilai |
|---|---|
| Publisher | `CN=red-Geck0` |
| Cert thumbprint (lokal di `Directory.Build.props`) | `CC77051FD762235A795F6C5F74788E03B6910498` |
| Publisher hash (turunan dari Publisher) | `1dnwtebwr9ekg` |
| Identity Name — main app | `cc4eb8d7-a694-4b39-be86-edccdf890305` |
| Identity Name — widget | `7e76dfc4-2f28-431a-adee-dc76fdef9b57` |

Package family name = `<Identity Name>_1dnwtebwr9ekg`, hardcoded di `PhantomKey.cpp`
dan `UpdateCheckService.cs`. Kalau Publisher diganti lagi, hash WAJIB dihitung ulang
(SHA-256 dari Publisher UTF-16LE → 8 byte pertama → base32 alfabet
`0123456789abcdefghjkmnpqrstvwxyz`).

> Referensi XFSET (path `C:\Program Files\8bit2qubit\...` + URL
> `8bit2qubit/XboxFullScreenExperienceTool`) sengaja DIPERTAHANKAN — tool eksternal
> nyata, bukan branding kita. LICENSE menyimpan "Required Notice" 8bit2qubit demi
> kepatuhan PolyForm Noncommercial (ditambah baris fork red-Geck0).

---

## 1. Nama yang muncul di "Installed Apps" / Start / FSE Home App

Yang tampil di daftar aplikasi & tile **bukan** `<Properties><DisplayName>`,
melainkan **Application VisualElements DisplayName** (+ untuk main app via
`ms-resource:AppDisplayName`). Author = `<PublisherDisplayName>`.

### Main app — `OmniConsole/Package.appxmanifest`
| Field | Nilai sekarang |
|---|---|
| `<Properties><DisplayName>` | `OmniConsoleMod` |
| `<Properties><PublisherDisplayName>` | `red-Geck0` |

Nama yang tampil di Installed Apps / Start / FSE diambil dari
`ms-resource:AppDisplayName` & `ms-resource:SettingsAppDisplayName` (lihat §3).

### Widget — `OmniConsole.PhantomLink/Package.appxmanifest`
| Field | Nilai sekarang |
|---|---|
| `<Properties><DisplayName>` | `OmniConsoleMod OmniCharm` |
| `<Properties><PublisherDisplayName>` | `red-Geck0` |
| `<uap:VisualElements DisplayName=...>` | `OmniConsoleMod OmniCharm` |
| `<uap:VisualElements Description=...>` | `OmniConsoleMod OmniCharm` |

---

## 2. Update-check (auto-update GitHub)

`OmniConsole/Services/UpdateCheckService.cs` — URL repo rilis. Sudah menunjuk ke
`red-Geck0/OmniConsoleMod` (GitHub API + halaman rilis).

### Nama file aset rilis (transisi ke nama baru)

Mulai rilis **sesudah v2.6.9.0**, updater memilih aset MSIX **tanpa melihat
prefix nama produk**. Aturannya:

| Aset | Syarat nama file |
|---|---|
| Widget (PhantomLink) | mengandung `_{versi}_` **dan** berakhiran `_x64-widget.msix` |
| Main app | mengandung `_{versi}_` **dan** berakhiran `_x64.msix` |

`{versi}` = tag rilis tanpa `v` (tag `v2.7.0.0` → `_2.7.0.0_`). Tag dan nama file
**harus pakai format versi yang sama** (4 angka); kalau tidak cocok, updater tidak
menemukan aset dan jatuh ke membuka halaman rilis di browser.

File unduhan sementara di LocalFolder selalu bernama `pending-update-main_*.msix` /
`pending-update-widget_*.msix`, jadi tidak bergantung pada nama aset.

**Urutan transisi:**
1. **Rilis transisi** (rilis pertama yang berisi aturan di atas): aset **masih**
   bernama `OmniConsole_{ver}_x64.msix` & `OmniConsole.PhantomLink_{ver}_x64-widget.msix`,
   karena versi ≤ 2.6.9.0 hanya mengenali prefix itu.
2. **Rilis sesudahnya**: aset boleh pakai nama baru, misalnya
   `AnyXboxMode_{ver}_x64.msix` & `AnyXboxMode.Widget_{ver}_x64-widget.msix`.
   Sesuaikan juga `Make-OmniConsoleMod-Release.ps1` (termasuk guard MSIX basi).
3. Pengguna yang melompati rilis transisi: updater lama tidak menemukan aset lalu
   membuka halaman rilis, dan mereka pasang manual dari zip (update in-place tetap
   jalan karena Identity sama). Opsional: selama 1–2 rilis unggah aset dengan dua
   nama (lama + baru).

---

## 3. Teks string in-app — `OmniConsole/Strings/{en-US,zh-CN,zh-TW}/Resources.resw`

Ubah ketiga bahasa (zh-CN / zh-TW boleh diabaikan kalau hanya pakai en-US; key
yang hilang fallback ke en-US). Key yang mengandung nama produk:

| Key resw | Nilai en-US sekarang |
|---|---|
| `AppDisplayName` | `OmniConsoleMod` |
| `SettingsAppDisplayName` | `OmniConsoleMod Settings` |
| `SettingsTitle` | `OmniConsoleMod Settings` |
| `AboutTitle` | `About OmniConsoleMod` |
| `AboutSection_Suite` | `OmniConsoleMod Suite` |
| `Label_OmniConsole` | `OmniConsoleMod` |
| `Update_Available` (+ varian Library/Start/Generic) | `OmniConsoleMod v{0} is available...` |
| `Update_LatestVersion` | `OmniConsoleMod is on the latest version.` |
| `Update_Updating` | `Updating OmniConsoleMod` |
| `Update_Downloading` | `Downloading OmniConsoleMod (2 of 2)...` |
| `Update_Installing` | `Installing OmniConsoleMod (2 of 2). The app will restart...` |

> Catatan: di dalam teks `Update_Available` masih ada frasa "Open **OmniConsole
> Settings** from Game Bar..." — ini sengaja, merujuk nama menu sistem.

### Belum di-rebrand (sengaja, opsional kalau mau konsisten penuh)
String prompt FSE berikut masih "OmniConsole":
`FseNotAvailable`, `FseHandheldRequired`, `FseHomeAppNotSet`, dan beberapa string
validasi platform. Boleh diganti ke OmniConsoleMod kalau mau, tidak wajib.

---

## 4. Cara build MSIX rilis (penting!)

- **Widget** (proyek UWP) → MSIX otomatis ke folder `*_Test` tiap kali build.
- **Main app** (proyek WindowsAppSDK) → MSIX **HANYA** lewat
  Visual Studio: klik-kanan project **OmniConsole** → *Package and Publish* →
  *Create App Packages* → Sideloading → Release/x64. `msbuild /t:Build` biasa
  **TIDAK** membuat ulang MSIX main app (gampang ke-ambil yang basi).

Script `Make-OmniConsoleMod-Release.ps1` (di luar repo) merakit folder rilis +
zip; ada guard yang menolak MSIX main app yang basi.
