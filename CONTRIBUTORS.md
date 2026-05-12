# Contributors

## Original Project
- **[8bit2qubit](https://github.com/8bit2qubit)** — Creator and original maintainer of [OmniConsole](https://github.com/8bit2qubit/OmniConsole)

## This Fork (red-Geck0)

This is an enhanced fork of OmniConsole with additional gamepad navigation improvements and virtual keyboard integration.

**Maintainer**: [red-Geck0](https://github.com/red-Geck0)  
**Active Development Period**: May 2026 – May 2026

### Key Contributions & Enhancements

#### Virtual Keyboard Integration
- Added two Windows 11 touch keyboard activation methods to Map Button dialog Special actions:
  - **Touch Keyboard (Method 1 – COM)**: Uses `ITipInvocation::Toggle()` for native Windows 11 Touch Keyboard
  - **On-Screen Keyboard (Method 2 – osk.exe)**: Fallback using `osk.exe` for broader compatibility

#### Settings Page Restructuring
- Embrace Gamepad Mouse Mode. Rename it to **OmniNav Mode** across all UI and expand its capabilities:
  - Added **Whitelist/Blacklist** mode with user-editable app lists for fine-grained control
  - Support for dual layouts: **Lefty** (left stick = cursor, formerly know as OmniNav layout) and **Righty** (right stick = cursor, formerly known as Classic layout)
- Created dedicated **OmniNav Mode** settings page separating gamepad-to-mouse emulation from other advanced settings
- Implemented **Layered Mode** feature, which makes OmniNav Mode only activates when assigned button is held:
  - Per-button custom keyboard/action mapping
  - 3-second hold activation trigger with double-beep audio feedback
---

## Attribution

All original OmniConsole code and features remain credited to **8bit2qubit**.  
Modifications and enhancements above are the work of **red-Geck0**.

For questions or issues related to this fork, please refer to the fork's repository.  
For the original project, visit [https://github.com/8bit2qubit/OmniConsole](https://github.com/8bit2qubit/OmniConsole).
