# OmniConsole (Enhanced Fork)

<p align="center">
<img src="OmniConsole/Assets/SplashScreen.scale-200.png" alt="OmniConsole" style="height: 80px; object-fit: contain; display: block; margin: 0 auto;">
</p>

<p align="center">
<a href="https://github.com/8bit2qubit/OmniConsole/releases/latest"><img src="https://img.shields.io/github/v/release/8bit2qubit/OmniConsole?style=flat&color=blue" alt="Latest Release"></a>
<a href="#"><img src="https://img.shields.io/badge/tech-C%23%20%26%20C%2B%2B%20%7C%20.NET%208%20%7C%20WinUI%203-blueviolet.svg?style=flat" alt="Tech"></a>
<a href="https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE"><img src="https://img.shields.io/github/license/8bit2qubit/OmniConsole?style=flat" alt="License"></a>
</p>

> 📌 **This is an enhanced fork** of [OmniConsole](https://github.com/8bit2qubit/OmniConsole) by [8bit2qubit](https://github.com/8bit2qubit).  
> For full project details, features, installation, and usage — see the [**original README**](https://github.com/8bit2qubit/OmniConsole?tab=readme-ov-file#readme).

**Maintained by**: [red-Geck0](https://github.com/red-Geck0)

---

## What's Different in This Fork

This fork focuses on **gamepad UX improvements**, **OmniNav (mouse mode) refinements**, and **settings UI polish** on top of the original OmniConsole.

### Enhancements (May 2024 – May 2026)

**OmniNav (Gamepad Mouse Mode)**
- Dedicated OmniNav settings page with General / Input Mapping tabs (LB/RB switching)
- Whitelist/Blacklist mode — user-editable app lists for fine-grained control
- Dual layout support: **Lefty** (left stick = cursor) / **Righty** (right stick = cursor)
- Per-layout custom button mappings with save/load config (layout-agnostic JSON)
- Layered Mode — hold-to-activate custom mappings with audio feedback
- Virtual Keyboard as special action (Windows 11 Touch Keyboard via COM + osk.exe fallback)
- "(active)" indicator on the currently active layout in Input Mapping

**Gamepad Navigation**
- Fixed D-Pad focus flow across SelectorBar, RadioButtons, ComboBox, and dialogs
- Right-stick scrolling in dropdowns and settings panels (30 FPS)
- Optimized polling: 33ms tick, 32px/tick scroll, 50ms repeat interval
- Cross-section D-Pad navigation with proper focus isolation during dialogs

**Settings UI**
- Centered SelectorBar tabs for platform categories (System/Custom) and OmniNav sections
- Compact nav rail with proper spacing
- Import button repositioned below card area
- About page with environment snapshot and PhantomKey health check
- Gamepad button glyphs rendered with Segoe Fluent Icons

**OmniCharm Widget**
- Updated labels: OmniNav Mode, Off/Whitelisted/Blacklisted, Lefty/Righty
- Synced with OmniNav settings page values

**Other**
- Renamed "Gamepad Mouse Mode" → "OmniNav Mode" across all UI (EN/zh-TW/zh-CN)
- Steam Big Picture detection via window-style + monitor-size heuristic
- File Explorer D-Pad skip to avoid double-jump with native gamepad navigation
- Custom platform support: One Game Launcher, MSI Center M

---

## Local Development

```bash
git clone https://github.com/red-Geck0/OmniConsoleMod.git
cd OmniConsoleMod
```

Open `OmniConsole.sln` with Visual Studio 2022+ (17.8+). Requires the **WinUI application development** workload. Build as `Debug | x64` and press F5.

---

## License

[GNU General Public License v3.0 (GPL-3.0)](https://github.com/8bit2qubit/OmniConsole/blob/main/LICENSE) — same as the original project.
