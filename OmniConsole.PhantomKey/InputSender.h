#pragma once
#include <windows.h>
#include <string>
#include <vector>

// PhantomKey 送出的每一筆 INPUT 都帶上此標記（ki.dwExtraInfo / mi.dwExtraInfo），
// 讓 CursorConflict 的低階滑鼠掛鉤能把自己送的事件與外部注入區分開。
constexpr ULONG_PTR kPhantomKeyInputTag = 0x504B4559;  // 'PKEY'

// 解析簡易快速鍵字串（如 "Ctrl+1"、"Shift+Tab"）為 VK code 序列
std::vector<WORD> ParseCombo(const std::wstring& combo);

// 送出鍵盤快速鍵組合
void SendKeyCombo(const std::wstring& combo);
