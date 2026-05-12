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

### Enhancements (May 2026)

**OmniNav (formerly know as Gamepad Mouse Mode)**
- Dedicated OmniNav settings page
- Whitelist/Blacklist mode — user-editable app lists for fine-grained control
- Dual layout support: **Lefty** (left stick = cursor, formerly know as OmniNav layout) / **Righty** (right stick = cursor, formerly known as Classic layout)
- Per-layout custom button mappings with save/load config (layout-agnostic JSON)
- Layered Mode — hold-to-activate custom mappings with audio feedback
- Virtual Keyboard as special action (Windows 11 Touch Keyboard via COM + osk.exe fallback)

**Other**
- Fixed D-Pad focus flow across settings pages
- Misc UI polish.
- Updated labels on OmniCharm Widget: OmniNav Mode, Off/Whitelisted/Blacklisted, Lefty/Righty
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
