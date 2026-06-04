# Rebranding Notes

Catatan semua titik yang menentukan **nama produk** dan **author** yang tampil ke
pengguna. Ubah di sini kalau mau ganti nama / author. Nama saat ini:

- Nama produk: **OmniConsoleMod**
- Author / publisher display: **red-Geck0**
- Repo update-check: **red-Geck0/OmniConsoleMod**

> PENTING — JANGAN diubah saat rebranding (kalau diubah, update in-place rusak &
> setting pengguna hilang):
> - `<Identity Name=...>` di kedua `Package.appxmanifest`
> - `Publisher="CN=8bit2qubit"` di kedua manifest (harus sama dengan subject
>   sertifikat penandatangan)
> - Sertifikat penandatangan (`.cer` / `.pfx`, `CN=8bit2qubit`)
> - JSON `"shell": "OmniConsole"` (ini fungsional, bukan teks tampilan)

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
