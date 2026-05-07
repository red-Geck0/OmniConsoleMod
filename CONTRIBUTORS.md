# Contributors

## Original Project
- **[8bit2qubit](https://github.com/8bit2qubit)** — Creator and original maintainer of [OmniConsole](https://github.com/8bit2qubit/OmniConsole)

## This Fork (red-Geck0)

This is an enhanced fork of OmniConsole with additional gamepad navigation improvements and virtual keyboard integration.

**Maintainer**: [red-Geck0](https://github.com/red-Geck0)  
**Active Development Period**: May 2024 – May 2026

### Key Contributions & Enhancements

#### Gamepad Navigation & Controller Support
- **XY Focus Navigation Fixes**
  - Fixed SelectorBar being skipped during D-Pad navigation (horizontal center alignment conflict with right-aligned controls)
  - Added explicit `XYFocusDown`/`XYFocusUp` bindings on critical controls for seamless D-Pad flow
  - Improved RadioButtons control in dialogs — D-Pad Left/Right now properly inject arrow keys for intra-group navigation

- **ComboBox Interaction Improvements**
  - Auto-scroll focused items into view when navigating via D-Pad in dropdowns
  - Added right-stick Y-axis scrolling support inside ComboBox popups (same pixel-rate as page scrolling)
  - Helper method `FindComboBoxPopupScrollViewer()` to locate internal ScrollViewer in ComboBox popups

- **Dialog Navigation Enhancements**
  - Improved ContentDialog focus isolation and keyboard navigation routing
  - Fixed button press handlers to respect dialog modal state (`_isDialogOpen` flag)
  - Implemented `SuppressFocusEnforcement` property to prevent GamepadNavigationService from stealing focus from dialogs

- **Gamepad Polling Optimization**
  - Increased polling frequency from 50ms to 33ms (~30 FPS)
  - Increased right-stick scroll rate: 18px/tick → 32px/tick for smoother scrolling
  - Improved repeat key interval: 80ms → 50ms for faster continuous input

#### Virtual Keyboard Integration
- Added two Windows 11 touch keyboard activation methods to Map Button dialog Special actions:
  - **Touch Keyboard (Method 1 – COM)**: Uses `ITipInvocation::Toggle()` for native Windows 11 Touch Keyboard
  - **On-Screen Keyboard (Method 2 – osk.exe)**: Fallback using `osk.exe` for broader compatibility
- Research & implementation of COM interfaces (`CLSID {4CE576FA...}`, `IID {37C994E7...}`) for Windows 11 Touch Keyboard
- Platform detection and error handling for both methods with multi-threaded launching

#### Settings Page Restructuring
- Created dedicated **Mouse Mode** settings page separating gamepad-to-mouse emulation from other advanced settings
- Implemented **Layered Mode** feature mockup:
  - Per-button custom keyboard/action mapping
  - Dual-layout support (OmniNav & Classic) with independent layer configuration
  - 3-second hold activation trigger with double-beep audio feedback
  - Visual state management: greyed-out controls when layer-trigger button is selected

#### Audio & User Feedback
- Added double-beep sound (880Hz + 1100Hz) on Layered Mode activation for tactile feedback
- Implemented audio feedback using `Beep()` on detached thread to prevent UI blocking

#### Code Quality & Compatibility
- Fixed build errors in `SettingsService.cs`: CS0136 (variable shadowing), CS1929 (Linq extension methods)
- Enhanced `MappingFormatter.cs` with virtual keyboard token support (`vkb_com`, `vkb_osk`)
- Maintained backward compatibility with existing code patterns and UI standards

---

## Attribution

All original OmniConsole code and features remain credited to **8bit2qubit**.  
Modifications and enhancements above are the work of **red-Geck0**.

For questions or issues related to this fork, please refer to the fork's repository.  
For the original project, visit [https://github.com/8bit2qubit/OmniConsole](https://github.com/8bit2qubit/OmniConsole).
