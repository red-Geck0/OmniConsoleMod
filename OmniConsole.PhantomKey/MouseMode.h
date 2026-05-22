#pragma once
#include <windows.h>
#include <xinput.h>
#include "GamepadProfiles.h"

// ============================================================================
// Gamepad Mouse Mode：手把映射為滑鼠＋鍵盤輸入（查 profile 的 Bindings 表）
// ============================================================================
//
// Mouse Mode 啟用且前景非系統黑名單時，由 PhantomKey 主迴圈每 tick 呼叫 Tick()，
// 傳入該前景解析到的 profile。離開該前景時呼叫 Reset() 清除累積狀態。
//
// KeyTap / KeyHold / MouseButton 走鏡像按住（按下 keydown、放開 keyup）。
// DPad 整組 / Stick Arrows / Stick WASD 走鏡像按住；profile.dpadAutoRepeat=true 時
// DPad 額外依 OS 鍵盤重複設定補 keydown（服務瀏覽器/Epic 等導覽類前景）。
// KeyCombo / MouseWheel / TouchKeyboard 為邊緣觸發：按下時送一次，不鏡像。
//
// Layered Mode（profile.layered.enabled）：映射僅在 layer 作用中才生效。
//   - HoldRelease：按住 triggerKey 達門檻才作用，放開即失效。
//   - DoubleTapToggle：雙擊 triggerKey 切換作用狀態。
//   triggerKey 在 Layered 啟用時被保留作切換用，其原映射不送出。
//
// 註：skipDpad=true 時跳過 D-pad 映射（前景對 D-pad 已有原生反應，避免雙跳）。
// ============================================================================

namespace MouseMode {

    // 套用一個 profile：每 tick 呼叫。內含 Layered Mode gate —
    // profile 啟用 Layered 且 layer 未作用時，本 tick 不送出任何輸入。
    void Tick(const XINPUT_GAMEPAD& pad, const GamepadProfile& profile, bool skipDpad);

    // 離開目標前景時清除滾輪累積、游標累積、長按/鏡像按住與 Layered 狀態
    void Reset();

}  // namespace MouseMode
