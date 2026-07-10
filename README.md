# OmniConsoleMod

> 🌐 **English** | [繁體中文](README.zh-TW.md) | [Bahasa Indonesia](README.id.md)

<p align="center">
<img src="OmniConsole/Assets/SplashScreen.scale-200.png" alt="OmniConsoleMod" style="height: 80px; object-fit: contain; display: block; margin: 0 auto;">
</p>

<p align="center">
  <img src="docs/images/app-settings.png" alt="OmniConsoleMod Settings" height="350"><img src="docs/images/widget-omnicharm.png" alt="OmniCharm Widget" height="350"><img src="docs/images/app-omninav.png" alt="OmniNav Gamepad Profiles" height="350"><img src="docs/images/app-omninav-profile-settings.png" alt="Profile Settings Editor" height="350">
</p>

<p align="center">
<a href="https://github.com/red-Geck0/OmniConsoleMod/releases/latest"><img src="https://img.shields.io/github/v/release/red-Geck0/OmniConsoleMod?style=flat&color=blue" alt="Latest Release"></a>
<a href="https://github.com/red-Geck0/OmniConsoleMod/releases"><img src="https://img.shields.io/github/downloads/red-Geck0/OmniConsoleMod/total?style=flat" alt="Total Downloads"></a>
<a href="#"><img src="https://img.shields.io/badge/tech-C%23%20%26%20C%2B%2B%20%7C%20.NET%208%20%7C%20WinUI%203-blueviolet.svg?style=flat" alt="Tech"></a>
<a href="https://github.com/red-Geck0/OmniConsoleMod/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-PolyForm%20NC%201.0.0-blue?style=flat" alt="License"></a>
</p>

## 💡 What is OmniConsoleMod?

OmniConsoleMod serves as your Windows 11 Xbox Mode (FSE) Home shell on PCs and handhelds (ROG Ally, Legion Go, etc.), replacing the built-in Windows Xbox App, with an OmniCharm Game Bar widget, OmniNav gamepad mapping profile system, and Steam shortcuts. Whenever Xbox Mode (FSE) activates, OmniConsoleMod launches your configured gaming platform. Any platform can be your Xbox Mode (FSE) Home — Steam, Xbox, Epic, Armoury Crate SE, Playnite, One Game Launcher, MSI Center M, Shift Game Launcher, or any app you add.

- **On boot**: With "Enter Xbox mode (FSE) on startup" enabled, your gaming platform launches automatically at boot.
- **When Xbox mode is active**: Press the **Xbox button** to open Game Bar, then go to the **Home** tab and select **"Home"** to launch your gaming platform, or **"Library"** to open OmniConsoleMod Settings.

---

## ⚙️ Prerequisites

OmniConsoleMod requires the **Full Handheld edition** of Xbox Mode (FSE). Microsoft is gradually rolling out a Limited PC edition to regular PCs — use [Xbox Full Screen Experience Tool (XFSET)](https://github.com/8bit2qubit/XboxFullScreenExperienceTool) to switch to the Full Handheld edition.

- **Desktops, Laptops, Tablets & Handhelds without the Full Handheld edition**: Run XFSET first.
- **Native Handheld Devices** (e.g., ROG Ally series, Legion Go): Already on the Full Handheld edition — install OmniConsoleMod directly.
- **Xbox Controller Required**: Game Bar, Xbox Mode (FSE), and all gamepad features require an Xbox-compatible (XInput) controller with an Xbox button.

---

## 🚀 Quick Start

### 1. Install OmniConsoleMod

Download the latest release from the [**Releases Page**](https://github.com/red-Geck0/OmniConsoleMod/releases/latest).

**Option A: Install.bat (Recommended)**

1.  Extract the `OmniConsoleMod_*_x64.zip` file and run `Install.bat`. It will enable Developer Mode, install the certificate, install any missing framework dependencies, and install both MSIX packages automatically.

**Option B: Manual Install**

1.  **[Critical]** Go to **Windows Settings → System → Advanced** and enable **Developer Mode**.
2.  **[Critical]** Double-click the `.cer` file → click **Install Certificate** → Store Location: **Local Machine** → **Place all certificates in the following store** → Browse → select **Trusted People** → Finish.
3.  *(Optional — only needed on fresh/offline systems; online systems fetch these automatically)* Double-click each file inside `Dependencies\` to install the bundled framework packages (skip any that report an equal or newer version already installed).
4.  Double-click `OmniConsoleMod_*_x64.msix` to install the main app.
5.  Double-click `OmniConsoleMod.OmniCharm_*_x64-widget.msix` to install the OmniCharm widget.

### 2. Configure Your Default Platform

OmniConsoleMod will present the Settings UI on **first launch** or **after app updates**. You can also open it manually anytime from the Start Menu:

1.  Open **"OmniConsoleMod Settings"** from the Start Menu (All Apps).
2.  Select your preferred gaming platform from the card grid using a **mouse**, **touch**, or **Xbox controller** (**D-Pad/Left Stick** to navigate in all four directions, **A** to confirm):
    - **Steam Big Picture**
    - **Xbox App**
    - **Epic Games Store**
    - **Armoury Crate SE**
    - **Playnite Fullscreen**
    - **One Game Launcher**
    - **MSI Center M**
    - **Shift Game Launcher**

    Your selection is saved automatically. Press **B** on your controller or click/press **Exit** to finish.

### 3. [Critical] Set as Xbox Mode (FSE) Home App

<p>
  <img src="docs/images/fse-settings.png" alt="Xbox mode (FSE) Settings" height="221">
</p>

1.  Go to **Windows Settings → Gaming → Xbox mode (FSE)**.
2.  Set "Choose home app" to **OmniConsoleMod**.
3.  Enable **"Enter Xbox mode (FSE) on startup"**.

### 4. Done!

Your gaming platform now launches via any of these entry points:

- **Game Bar**: Press the **Xbox button**, then select **"Home"** to launch your gaming platform, or **"Library"** to open OmniConsoleMod Settings.
- **Boot**: Enable **"Enter Xbox mode (FSE) on startup"** for automatic launch at boot.
- **Start Menu**: Launch OmniConsoleMod directly to automatically activate Xbox Mode (FSE).

---

## ✨ Features

- **Automatic platform launch** – Your configured gaming platform launches automatically whenever Xbox Mode (FSE) activates.
- **Automatic Xbox Mode (FSE) entry** – When you launch OmniConsoleMod outside Xbox Mode (FSE) (e.g., from the Start Menu), it automatically triggers the Xbox Mode (FSE) entry dialog.
- **Multi-platform support** – Built-in support for **Steam Big Picture**, **Xbox App**, **Epic Games Store**, **Armoury Crate SE**, **Playnite Fullscreen**, **One Game Launcher**, **MSI Center M**, and **Shift Game Launcher** with Custom platform support (experimental).
- **Game Bar integration** – Game Bar's **"Home"** button launches your gaming platform; **"Library"** opens OmniConsoleMod Settings.
- **Troubleshoot page** – A dedicated page for Xbox Mode (FSE) recovery: restarts Game Bar to fix issues such as the "Restart for better performance" dialog not appearing, then enters Xbox Mode (FSE).
- **Gamepad first UI** – The app can be fully navigated by using gamepad.
- **OmniNav — Unified Gamepad Profiles & Mapping** – Map gamepad inputs to keyboard and mouse actions with a toggle switch (**On** / **Off**). Managed through reusable named mapping profiles, allowing customizable bindings, cursor speed, and navigation settings per profile.
- **Default Profiles** – Comes with built-in profiles including **OmniNav** (read-only), **Classic** (read-only), **Gaming** (editable, layered mode enabled), and **None** (completely disables mapping for native gamepad support in games).
- **Layered Mode** – Activate/deactivate secondary bindings on the fly in custom profiles (such as the default **Gaming** profile) by holding a trigger key (like Right Stick `RS`) or double-tapping it to toggle.
- **Touch Keyboard Action** – Support mapping a button to launch the Windows virtual keyboard (Touch Keyboard) via TabTip COM or OSK.
- **OmniCharm widget** – A Game Bar widget for in-game quick access. Open **Task View**, the **Xbox Library**, or the **Steam Overlay** in one tap; toggle **OmniNav (Gamepad Mouse Mode)**, assign/switch mapping profiles for the active app on the fly, and toggle the **Steam In-Game Overlay** (long-press ☰).
- **Native Xbox Mode (FSE) integration** – Registered as a Windows 11 Xbox Mode (FSE) Home App through the official API.
- **In-app updates** – Automatic checks for the latest GitHub releases, with download and install built into the Advanced settings page.

---

## ⚔️ OmniNav (OmniConsoleMod) vs Nekomata (OmniConsole Upstream)

| Comparison Dimension | Nekomata (OmniConsole Upstream) | OmniNav (OmniConsoleMod) |
|---|---|---|
| **Architecture Model** | Bound directly per-application (per-app mapping). | **Unified Profile Model**: Mappings are managed as reusable Named Profiles and then assigned to apps. |
| **Gamepad Mouse Mode** | Global setting with 3 modes: Off, Auto, Force On. Layout (OmniNav/Classic) and cursor speed are set globally via INI. | Simplified to a single global **On / Off** switch. Layout, cursor speed, and sensitivity are configured per-profile. |
| **Input Blocker** | Has an **Input Blocker** (blocks original gamepad inputs to prevent double inputs in games). | No strict input blocker. Designed as a **helper** to trigger shortcuts/mods during gameplay or navigate non-game apps easily. |
| **Layered Mode Nature** | None. Only supports a single static mapping layer. | **Yes**: Serves as an on-the-fly enabling/disabling the button mappings by holding or double-tapping a designated trigger key (e.g. `RS`). |
| **Game & Fullscreen Detection** | Relies on static per-app configurations. | Has **Game & Fullscreen Detection** to automatically apply the 'Game Default' (e.g., Gaming profile) or 'App Default' profile to new apps/games. |
| **Additional Actions** | Mappings are limited to standard keyboard/mouse buttons. | Supports system actions like launching the **Touch Keyboard** via TabTip COM or OSK. |
| **Widget Integration** | Global layout toggling and per-app configuration in separate dialogs. | Instant profile assignment via a dropdown for the active app, plus a shortcut button to open the editor. |

---

## 📖 Mini Guide: Using OmniNav & Profiles

### 1. Toggle OmniNav On/Off
Open the **OmniCharm Widget** in the Xbox Game Bar (Win + G or Xbox Button) and use the main toggle switch to turn OmniNav **On** or **Off**.

### 2. Quick App Assignment
When using any application:
1. Open the **OmniCharm Widget**.
2. Under **Foreground App**, you will see the active app's name.
3. Select a profile from the dropdown (e.g., select `Gaming`, `OmniNav`, or `None` to disable the button mapping feature).
4. The profile is automatically assigned to the game/app and applied whenever that app is in focus.

### 3. Understanding the Default Profiles
OmniConsoleMod uses game and fullscreen detection to apply fallback profiles:
- **App Default**: Applied to normal windowed apps that do not have a specific profile assigned. (Default: `OmniNav`).
- **Game Default**: Applied automatically to detected games or fullscreen applications that do not have a specific profile assigned. (Default: `Gaming`).
- You can change these defaults in **OmniConsoleMod Settings -> OmniNav -> Profiles**.

### 4. Using Layered Mode
The **Gaming** profile has **Layered Mode** enabled by default using Right Stick (`RS`) as the trigger.
- **Action**: A. Hold `RS` (1.6s) to activate Layered Mode, release `RS` to deactivate; B. Double-tap `RS` to activate Layered Mode, double-tap again to deactivate.
- This is useful for mapping quick system shortcuts to the controller buttons while in a game, then toggling them off to return to normal controls.
- Example Layered Mode use cases: open/close OSDs (RTSS, NVIDIA, Steam Overlay, etc.), open/toggle game mods (Lossless Scaling, Optiscaler, SpecialK, etc.), bring up the Virtual Keyboard to enter a game character name, start/stop/toggle-fullscreen in a console emulator.
- Note: the 'Gaming' profile here is just a standard profile with Layered Mode enabled and named 'Gaming'. So you don't have to use this profile for every game.

### 5. Creating & Editing Profiles
1. Open **OmniConsoleMod Settings** from the Start Menu.
2. Go to the **Gamepad Profiles** tab.
3. Select an existing profile to edit its settings and key bindings, press **Y** to set as default, or press **X** to create a custom profile.
4. Press **Copy from...** to duplicate configurations from read-only profiles like `OmniNav` or `Classic`.

---

## 🔄 How to Revert

> ⚠️ **Change the Xbox Mode (FSE) Home App setting _before_ uninstalling OmniConsoleMod.** If OmniConsoleMod is removed while it is still set as the Xbox Mode (FSE) Home App, Windows **Task View will stop working** on some builds. This is a bug in Windows itself.

1. Go to **Windows Settings → Gaming → Xbox mode (FSE)**.
2. Set "Choose home app" to **Xbox** or **None**.
3. Right-click **OmniConsoleMod** in the Start Menu and select **Uninstall**, or go to **Windows Settings → Apps → Installed apps** to uninstall it.
4. Go to **Windows Settings → Apps → Installed apps** and uninstall **OmniConsoleMod OmniCharm** (the widget does not appear in the Start Menu).

---

## 🛠️ Troubleshooting

If you run into issues caused by a Windows bug, such as Game Bar failing to open or the "Restart for better performance" dialog not appearing when entering Xbox Mode (FSE):

1. Open **OmniConsoleMod Settings** from the Start Menu.
2. Navigate to the **Troubleshoot** tab using the left menu.
3. Click the **"Run"** button next to **"Restart Game Bar & Enter Xbox Mode (FSE)"**. This restarts Game Bar and enters Xbox Mode (FSE); once Game Bar is restarted, the dialog appears as expected.

---

## 💻 Tech Stack


- **Primary Stack**: C# & .NET 8, C++
- **UI Framework**: WinUI 3
- **Packaging**: MSIX

---

## 🛠️ Local Development

1.  **Clone the Repository**

    ```bash
    git clone https://github.com/red-Geck0/OmniConsoleMod.git
    cd OmniConsole
    ```

2.  **Open in Visual Studio**

    Open `OmniConsole.sln` with Visual Studio 2026 (18.0+). Ensure the **WinUI application development** workload is installed.

3.  **Run for Development**

    Set the build configuration to `Debug`, select your platform (`x64`), and press `F5`.

---

## 📄 License

OmniConsole is licensed under the [PolyForm Noncommercial License 1.0.0](https://github.com/red-Geck0/OmniConsoleMod/blob/main/LICENSE).

You are free to use, modify, and redistribute OmniConsole for personal and nonprofit use under the same license. For the full terms, see the [official PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0).
