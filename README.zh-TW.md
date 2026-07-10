# OmniConsoleMod

> 🌐 [English](README.md) | **繁體中文** | [Bahasa Indonesia](README.id.md)

<p align="center">
<img src="OmniConsole/Assets/SplashScreen.scale-200.png" alt="OmniConsoleMod" style="height: 80px; object-fit: contain; display: block; margin: 0 auto;">
</p>

<p align="center">
  <img src="docs/images/app-settings.png" alt="OmniConsoleMod 設定介面" height="350"><img src="docs/images/widget-omnicharm.png" alt="OmniCharm 小工具" height="350"><img src="docs/images/app-omninav.png" alt="OmniNav 手把設定檔" height="350"><img src="docs/images/app-omninav-profile-settings.png" alt="設定檔編輯器" height="350">
</p>

<p align="center">
<a href="https://github.com/red-Geck0/OmniConsoleMod/releases/latest"><img src="https://img.shields.io/github/v/release/red-Geck0/OmniConsoleMod?style=flat&color=blue" alt="最新版本"></a>
<a href="https://github.com/red-Geck0/OmniConsoleMod/releases"><img src="https://img.shields.io/github/downloads/red-Geck0/OmniConsoleMod/total?style=flat" alt="總下載次數"></a>
<a href="#"><img src="https://img.shields.io/badge/tech-C%23%20%26%20C%2B%2B%20%7C%20.NET%208%20%7C%20WinUI%203-blueviolet.svg?style=flat" alt="技術"></a>
<a href="https://github.com/red-Geck0/OmniConsoleMod/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-PolyForm%20NC%201.0.0-blue?style=flat" alt="授權"></a>
</p>

## 💡 OmniConsoleMod 是什麼？

OmniConsoleMod 作為你在 PC 與掌機（ROG Ally、Legion Go 等）上的 Windows 11 Xbox 模式 (FSE) Home shell，取代 Windows 內建的 Xbox App，內含 OmniCharm Game Bar 小工具、OmniNav 手把映射設定檔系統，以及 Steam 捷徑。每當 Xbox 模式 (FSE) 啟動時，OmniConsoleMod 會啟動你設定的遊戲平台。任何平台都能成為你的 Xbox 模式 (FSE) Home — Steam、Xbox、Epic、Armoury Crate SE、Playnite、One Game Launcher、MSI Center M、Shift Game Launcher，或任何你新增的 app。

- **開機時**：啟用「開機時進入 Xbox 模式 (FSE)」後，你的遊戲平台會在開機時自動啟動。
- **Xbox 模式啟用時**：按下 **Xbox 按鈕**開啟 Game Bar，然後前往 **Home** 分頁並選擇 **「Home」** 來啟動遊戲平台，或 **「Library」** 來開啟 OmniConsoleMod 設定。

---

## ⚙️ 先決條件

OmniConsoleMod 需要 Xbox 模式 (FSE) 的**完整掌機版 (Full Handheld edition)**。Microsoft 正逐步將受限 PC 版推送至一般 PC — 請使用 [Xbox Full Screen Experience Tool (XFSET)](https://github.com/8bit2qubit/XboxFullScreenExperienceTool) 切換至完整掌機版。

- **桌機、筆電、平板與沒有完整掌機版的掌機**：先執行 XFSET。
- **原生掌機裝置**（如 ROG Ally 系列、Legion Go）：已是完整掌機版 — 直接安裝 OmniConsoleMod。
- **需要 Xbox 手把**：Game Bar、Xbox 模式 (FSE) 與所有手把功能都需要相容 Xbox（XInput）且具備 Xbox 按鈕的手把。

---

## 🚀 Quick Start

### 1. 安裝 OmniConsoleMod

從 [**Releases 頁面**](https://github.com/red-Geck0/OmniConsoleMod/releases/latest) 下載最新版本。

**選項 A：Install.bat（建議）**

1.  解壓縮 `OmniConsoleMod_*_x64.zip` 並執行 `Install.bat`。它會啟用開發者模式、安裝憑證、安裝任何缺少的框架相依套件，並自動安裝兩個 MSIX 套件。

**選項 B：手動安裝**

1.  **[關鍵]** 前往 **Windows 設定 → 系統 → 進階**並啟用**開發者模式**。
2.  **[關鍵]** 雙擊 `.cer` 檔案 → 點擊**安裝憑證** → 存放位置：**本機電腦** → **將所有憑證放入以下的存放區** → 瀏覽 → 選擇 **受信任的人員** → 完成。
3.  *(選用 — 僅在全新/離線系統需要；連網系統會自動取得)* 雙擊 `Dependencies\` 內的每個檔案以安裝隨附的框架套件（若已安裝相同或更新版本則略過）。
4.  雙擊 `OmniConsoleMod_*_x64.msix` 安裝主程式。
5.  雙擊 `OmniConsoleMod.OmniCharm_*_x64-widget.msix` 安裝 OmniCharm 小工具。

### 2. 設定你的預設平台

OmniConsoleMod 會在**首次啟動**或**App 更新後**顯示設定 UI。你也可以隨時從開始功能表手動開啟：

1.  從開始功能表（所有應用程式）開啟 **「OmniConsoleMod 設定」**。
2.  使用**滑鼠**、**觸控**或 **Xbox 手把**從卡片格選擇你偏好的遊戲平台（**D-Pad/左類比**四向導航，**A** 確認）：
    - **Steam Big Picture**
    - **Xbox App**
    - **Epic Games Store**
    - **Armoury Crate SE**
    - **Playnite Fullscreen**
    - **One Game Launcher**
    - **MSI Center M**
    - **Shift Game Launcher**

    你的選擇會自動儲存。在手把上按 **B** 或點擊/按下 **Exit** 完成。

### 3. [關鍵] 設為 Xbox 模式 (FSE) Home App

<p>
  <img src="docs/images/fse-settings.png" alt="Windows Xbox 模式 (FSE) 設定" height="221">
</p>

1.  前往 **Windows 設定 → 遊戲 → Xbox 模式 (FSE)**。
2.  將「選擇主畫面應用程式」設為 **OmniConsoleMod**。
3.  啟用 **「開機時進入 Xbox 模式 (FSE)」**。

### 4. 完成！

你的遊戲平台現在可透過以下任一入口啟動：

- **Game Bar**：按 **Xbox 按鈕**，然後選擇 **「Home」** 啟動遊戲平台，或 **「Library」** 開啟 OmniConsoleMod 設定。
- **開機**：啟用 **「開機時進入 Xbox 模式 (FSE)」** 以在開機時自動啟動。
- **開始功能表**：直接啟動 OmniConsoleMod 以自動進入 Xbox 模式 (FSE)。

---

## ✨ 功能特色

- **自動啟動平台** – 每當 Xbox 模式 (FSE) 啟動時，自動啟動你設定的遊戲平台。
- **自動進入 Xbox 模式 (FSE)** – 當你在 Xbox 模式 (FSE) 之外（例如從開始功能表）啟動 OmniConsoleMod 時，會自動觸發 Xbox 模式 (FSE) 進入對話框。
- **多平台支援** – 內建支援 **Steam Big Picture**、**Xbox App**、**Epic Games Store**、**Armoury Crate SE**、**Playnite Fullscreen**、**One Game Launcher**、**MSI Center M** 與 **Shift Game Launcher**，並支援自訂平台（實驗性）。
- **Game Bar 整合** – Game Bar 的 **「Home」** 按鈕啟動你的遊戲平台；**「Library」** 開啟 OmniConsoleMod 設定。
- **疑難排解頁面** – 專屬於 Xbox 模式 (FSE) 修復的頁面：重啟 Game Bar 以修復如「重新啟動以獲得更好效能」對話框未出現等問題，然後進入 Xbox 模式 (FSE)。
- **手把優先 UI** – App 可完全使用手把導航。
- **OmniNav — 統一手把設定檔與映射** – 透過開關（**On** / **Off**）將手把輸入映射為鍵盤與滑鼠動作。以可重用的具名映射設定檔管理，每個設定檔可自訂按鍵綁定、游標速度與導航設定。
- **預設設定檔** – 內建設定檔包括 **OmniNav**（唯讀）、**Classic**（唯讀）、**Gaming**（可編輯，啟用 layered mode）與 **None**（完全停用映射，供遊戲使用原生手把）。
- **Layered Mode** – 在自訂設定檔（例如預設的 **Gaming** 設定檔）中，透過按住觸發鍵（如右類比 `RS`）或雙擊切換，即時啟用／停用次要綁定。
- **觸控鍵盤動作** – 支援將按鈕映射為透過 TabTip COM 或 OSK 啟動 Windows 虛擬鍵盤（觸控鍵盤）。
- **OmniCharm 小工具** – 遊戲中快速存取的 Game Bar 小工具。一鍵開啟 **Task View**、**Xbox Library** 或 **Steam Overlay**；切換 **OmniNav（手把滑鼠模式）**、即時為使用中的 App 指派／切換映射設定檔，並切換 **Steam 遊戲內 Overlay**（長按 ☰）。
- **原生 Xbox 模式 (FSE) 整合** – 透過官方 API 註冊為 Windows 11 Xbox 模式 (FSE) Home App。
- **App 內更新** – 自動檢查最新的 GitHub 發布，下載與安裝功能內建於進階設定頁面。

---

## ⚔️ OmniNav (OmniConsoleMod) vs Nekomata (OmniConsole Upstream)

| 比較維度 | Nekomata (OmniConsole 上游) | OmniNav (OmniConsoleMod) |
|---|---|---|
| **架構模型** | 直接以應用程式為單位綁定（per-app 映射）。 | **統一設定檔模型**：映射以可重用的具名設定檔管理，再指派給 App。 |
| **手把滑鼠模式** | 全域設定，3 種模式：Off、Auto、Force On。版面（OmniNav/Classic）與游標速度透過 INI 全域設定。 | 簡化為單一全域 **On / Off** 開關。版面、游標速度與靈敏度改為每個設定檔各自設定。 |
| **輸入阻擋器** | 具有 **Input Blocker**（阻擋原始手把輸入，避免遊戲中雙重輸入）。 | 無嚴格的輸入阻擋器。設計為**輔助工具**，用於遊戲中觸發捷徑/mod，或輕鬆導航非遊戲 App。 |
| **Layered Mode 性質** | 無。僅支援單一靜態映射層。 | **有**：透過按住或雙擊指定的觸發鍵（如 `RS`），即時啟用/停用按鍵映射。 |
| **遊戲與全螢幕偵測** | 依賴靜態的 per-app 設定。 | 具備**遊戲與全螢幕偵測**，自動為新的 App/遊戲套用「Game Default」（如 Gaming 設定檔）或「App Default」設定檔。 |
| **額外動作** | 映射僅限標準鍵盤/滑鼠按鍵。 | 支援系統動作，例如透過 TabTip COM 或 OSK 啟動**觸控鍵盤**。 |
| **小工具整合** | 全域版面切換與 per-app 設定分散在不同對話框。 | 為使用中的 App 透過下拉選單即時指派設定檔，並有捷徑按鈕開啟編輯器。 |

---

## 📖 簡易指南：使用 OmniNav 與設定檔

### 1. 切換 OmniNav 開/關
在 Xbox Game Bar（Win + G 或 Xbox 按鈕）中開啟 **OmniCharm 小工具**，使用主開關將 OmniNav 切換為 **On** 或 **Off**。

### 2. 快速指派 App
使用任何應用程式時：
1. 開啟 **OmniCharm 小工具**。
2. 在 **Foreground App** 下，你會看到使用中 App 的名稱。
3. 從下拉選單選擇設定檔（例如選 `Gaming`、`OmniNav`，或 `None` 以停用按鍵映射功能）。
4. 該設定檔會自動指派給遊戲/App，並在該 App 取得焦點時套用。

### 3. 認識預設設定檔
OmniConsoleMod 使用遊戲與全螢幕偵測來套用備援設定檔：
- **App Default**：套用於沒有指定設定檔的一般視窗化 App。（預設：`OmniNav`）。
- **Game Default**：自動套用於偵測到的遊戲或全螢幕應用程式，且沒有指定設定檔者。（預設：`Gaming`）。
- 你可以在 **OmniConsoleMod 設定 -> OmniNav -> Profiles** 中變更這些預設值。

### 4. 使用 Layered Mode
**Gaming** 設定檔預設啟用 **Layered Mode**，以右類比（`RS`）為觸發鍵。
- **操作**：A. 按住 `RS`（1.6 秒）啟用 Layered Mode，放開 `RS` 停用；B. 雙擊 `RS` 啟用 Layered Mode，再次雙擊停用。
- 這很適合在遊戲中將快速系統捷徑映射到手把按鈕，然後再切換關閉以回到正常操作。
- Layered Mode 使用情境範例：開啟/關閉 OSD（RTSS、NVIDIA、Steam Overlay 等）、開啟/切換遊戲 mod（Lossless Scaling、Optiscaler、SpecialK 等）、叫出虛擬鍵盤以輸入遊戲角色名稱、在主機模擬器中 start/stop/切換全螢幕。
- 註：這裡的「Gaming」設定檔只是一個啟用 Layered Mode 並命名為「Gaming」的標準設定檔。所以你不必為每個遊戲都使用這個設定檔。

### 5. 建立與編輯設定檔
1. 從開始功能表開啟 **OmniConsoleMod 設定**。
2. 前往 **Gamepad Profiles** 分頁。
3. 選擇現有設定檔以編輯其設定與按鍵綁定，按 **Y** 設為預設，或按 **X** 建立自訂設定檔。
4. 按 **Copy from...** 從唯讀設定檔（如 `OmniNav` 或 `Classic`）複製設定。

---

## 🔄 如何還原

> ⚠️ **在解除安裝 OmniConsoleMod _之前_，先變更 Xbox 模式 (FSE) Home App 設定。** 若在 OmniConsoleMod 仍設為 Xbox 模式 (FSE) Home App 時將其移除，某些版本的 Windows **Task View 會停止運作**。這是 Windows 本身的 bug。

1. 前往 **Windows 設定 → 遊戲 → Xbox 模式 (FSE)**。
2. 將「選擇主畫面應用程式」設為 **Xbox** 或 **None**。
3. 在開始功能表右鍵點擊 **OmniConsoleMod** 並選擇**解除安裝**，或前往 **Windows 設定 → 應用程式 → 已安裝的應用程式**解除安裝。
4. 前往 **Windows 設定 → 應用程式 → 已安裝的應用程式**，解除安裝 **OmniConsoleMod OmniCharm**（小工具不會出現在開始功能表）。

---

## 🛠️ 疑難排解

若你遇到 Windows bug 造成的問題，例如 Game Bar 無法開啟，或進入 Xbox 模式 (FSE) 時「重新啟動以獲得更好效能」對話框未出現：

1. 從開始功能表開啟 **OmniConsoleMod 設定**。
2. 使用左側選單前往 **Troubleshoot** 分頁。
3. 點擊 **「Restart Game Bar & Enter Xbox Mode (FSE)」** 旁的 **「Run」** 按鈕。這會重啟 Game Bar 並進入 Xbox 模式 (FSE)；Game Bar 重啟後，對話框會如預期出現。

---

## 💻 技術堆疊


- **主要技術**：C# & .NET 8、C++
- **UI 框架**：WinUI 3
- **封裝**：MSIX

---

## 🛠️ Local Development

1.  **Clone 儲存庫**

    ```bash
    git clone https://github.com/red-Geck0/OmniConsoleMod.git
    cd OmniConsole
    ```

2.  **在 Visual Studio 中開啟**

    使用 Visual Studio 2026 (18.0+) 開啟 `OmniConsole.sln`。確保已安裝 **WinUI application development** 工作負載。

3.  **執行開發版本**

    將建置設定設為 `Debug`，選擇你的平台（`x64`），按 `F5`。

---

## 📄 授權

OmniConsole 採用 [PolyForm Noncommercial License 1.0.0](https://github.com/red-Geck0/OmniConsoleMod/blob/main/LICENSE) 授權。

你可以在相同授權下，為個人與非營利用途自由使用、修改與重新散布 OmniConsole。完整條款請見[官方 PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)。
