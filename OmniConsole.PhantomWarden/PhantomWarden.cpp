// ============================================================================
// PhantomWarden：系統管理員程式支援的安裝／移除工具
// ============================================================================
//
// 為什麼需要這支程式：
//   PhantomKey 的映射靠 SendInput 送出。UIPI 規定「低完整性等級的行程不得把
//   輸入送進高完整性等級的視窗」——前景是系統管理員權限的程式時，SendInput
//   會被靜默丟棄（回傳成功、GetLastError 也不會有錯），映射整個失效。
//   唯一的解法是讓 PhantomKey 本身也跑在 High IL。
//
// 做法：
//   註冊一個「以最高權限執行」的排程工作指向 PhantomKey，往後主程式（一般權限）
//   只要叫這個工作跑起來就能拿到 High IL 的 PhantomKey，不必每次都跳 UAC。
//   註冊工作本身需要系統管理員權限，因此這支程式的資訊清單標為
//   requireAdministrator，由主程式以 runas 動詞啟動一次。
//
// 安全性考量：
//   排程工作指向的執行檔放在 %ProgramData%\OmniConsoleMod\，並且明確設成
//   「Administrators/SYSTEM 完全控制、Users 只能讀取與執行」的保護型 DACL。
//   若把工作指向使用者可寫的目錄（例如 LocalAppData），任何一般權限的程式
//   都能改寫那個檔案，再叫工作跑起來就直接拿到系統管理員權限——那是提權漏洞，
//   不是功能。目錄權限這一步不能省。
//
// 用法：
//   OmniConsole.PhantomWarden.exe --install --sid <使用者SID> --source <來源exe> [--source2 <備援來源exe>]
//   OmniConsole.PhantomWarden.exe --uninstall
//
// 結束碼：0 成功，非 0 失敗（主程式據此顯示結果）。
// ============================================================================

#include <windows.h>
#include <shlobj.h>
#include <taskschd.h>
#include <aclapi.h>
#include <sddl.h>
#include <string>

#pragma comment(lib, "taskschd.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")

#include "WardenShared.h"

namespace {

// ── 小工具 ──────────────────────────────────────────────────────────────────

// %ProgramData%\OmniConsoleMod
std::wstring GetInstallDir() {
    PWSTR base = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &base))) return L"";
    std::wstring dir = std::wstring(base) + L"\\" + kWardenInstallFolderName;
    CoTaskMemFree(base);
    return dir;
}

std::wstring GetInstalledExePath() {
    std::wstring dir = GetInstallDir();
    return dir.empty() ? L"" : dir + L"\\" + kWardenPayloadExeName;
}

// 建立安裝目錄並套上保護型 DACL（不繼承父層權限）：
//   SYSTEM / Administrators 完全控制、Users 只能讀取與執行。
// 目錄已存在時仍會重新套用權限，避免舊版留下的寬鬆設定被沿用。
bool EnsureSecureDir(const std::wstring& dir) {
    if (!CreateDirectoryW(dir.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
        return false;

    // OICI = 物件繼承 + 容器繼承；PAI = 保護、不從父層繼承
    // 0x1200a9 = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)",
            SDDL_REVISION_1, &sd, nullptr))
        return false;

    BOOL daclPresent = FALSE, daclDefaulted = FALSE;
    PACL dacl = nullptr;
    bool ok = false;
    if (GetSecurityDescriptorDacl(sd, &daclPresent, &dacl, &daclDefaulted) && daclPresent) {
        DWORD rc = SetNamedSecurityInfoW(
            const_cast<LPWSTR>(dir.c_str()), SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
            nullptr, nullptr, dacl, nullptr);
        ok = (rc == ERROR_SUCCESS);
    }
    LocalFree(sd);
    return ok;
}

// 取命令列參數值；找不到回空字串
std::wstring GetArg(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 0; i + 1 < argc; ++i)
        if (_wcsicmp(argv[i], name) == 0) return argv[i + 1];
    return L"";
}

bool HasFlag(int argc, wchar_t** argv, const wchar_t* name) {
    for (int i = 0; i < argc; ++i)
        if (_wcsicmp(argv[i], name) == 0) return true;
    return false;
}

// ── 排程工作 ────────────────────────────────────────────────────────────────

// 產生工作定義 XML。principal 綁在傳入的 SID 上，工作只在該使用者互動登入時
// 以其可取得的最高權限執行；沒有任何觸發程序，只能由主程式隨選啟動。
std::wstring BuildTaskXml(const std::wstring& userSid, const std::wstring& exePath) {
    std::wstring xml;
    xml += L"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n";
    xml += L"<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n";
    xml += L"  <RegistrationInfo>\r\n";
    xml += L"    <Author>red-Geck0</Author>\r\n";
    xml += L"    <Description>Runs the OmniConsoleMod gamepad mapper with administrator rights so mappings reach apps that run as administrator.</Description>\r\n";
    xml += L"  </RegistrationInfo>\r\n";
    xml += L"  <Triggers />\r\n";
    xml += L"  <Principals>\r\n";
    xml += L"    <Principal id=\"Author\">\r\n";
    xml += L"      <UserId>" + userSid + L"</UserId>\r\n";
    xml += L"      <LogonType>InteractiveToken</LogonType>\r\n";
    xml += L"      <RunLevel>HighestAvailable</RunLevel>\r\n";
    xml += L"    </Principal>\r\n";
    xml += L"  </Principals>\r\n";
    xml += L"  <Settings>\r\n";
    xml += L"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n";
    xml += L"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n";
    xml += L"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n";
    xml += L"    <AllowHardTerminate>true</AllowHardTerminate>\r\n";
    xml += L"    <StartWhenAvailable>false</StartWhenAvailable>\r\n";
    xml += L"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n";
    xml += L"    <IdleSettings>\r\n";
    xml += L"      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n";
    xml += L"      <RestartOnIdle>false</RestartOnIdle>\r\n";
    xml += L"    </IdleSettings>\r\n";
    xml += L"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n";
    xml += L"    <Enabled>true</Enabled>\r\n";
    xml += L"    <Hidden>false</Hidden>\r\n";
    xml += L"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n";
    xml += L"    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>\r\n";
    xml += L"    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>\r\n";
    xml += L"    <WakeToRun>false</WakeToRun>\r\n";
    // PhantomKey 是常駐行程，不能有執行時間上限
    xml += L"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n";
    xml += L"    <Priority>5</Priority>\r\n";
    xml += L"  </Settings>\r\n";
    xml += L"  <Actions Context=\"Author\">\r\n";
    xml += L"    <Exec>\r\n";
    xml += L"      <Command>" + exePath + L"</Command>\r\n";
    xml += L"    </Exec>\r\n";
    xml += L"  </Actions>\r\n";
    xml += L"</Task>\r\n";
    return xml;
}

// 連上工作排程器並取得根資料夾
HRESULT ConnectTaskService(ITaskService** service, ITaskFolder** root) {
    *service = nullptr;
    *root = nullptr;

    HRESULT hr = CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_ITaskService, reinterpret_cast<void**>(service));
    if (FAILED(hr)) return hr;

    VARIANT empty;
    VariantInit(&empty);
    hr = (*service)->Connect(empty, empty, empty, empty);
    if (FAILED(hr)) return hr;

    BSTR rootPath = SysAllocString(L"\\");
    hr = (*service)->GetFolder(rootPath, root);
    SysFreeString(rootPath);
    return hr;
}

// 註冊（或更新）排程工作。
// sddl 明確授權目標使用者讀取與執行，這樣一般權限的主程式才叫得動它；
// 完全控制只留給 SYSTEM 與 Administrators，一般權限無法改寫工作定義。
HRESULT RegisterElevatedTask(const std::wstring& userSid, const std::wstring& exePath) {
    ITaskService*    service = nullptr;
    ITaskFolder*     root    = nullptr;
    ITaskFolder*     folder  = nullptr;
    IRegisteredTask* task    = nullptr;

    HRESULT hr = ConnectTaskService(&service, &root);
    if (SUCCEEDED(hr)) {
        BSTR folderName = SysAllocString(kWardenTaskFolder);
        VARIANT noSddl;
        VariantInit(&noSddl);
        hr = root->CreateFolder(folderName, noSddl, &folder);
        if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS))
            hr = root->GetFolder(folderName, &folder);
        SysFreeString(folderName);
    }

    if (SUCCEEDED(hr)) {
        std::wstring xml  = BuildTaskXml(userSid, exePath);
        std::wstring sddl = std::wstring(L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;") + userSid + L")";

        BSTR bName = SysAllocString(kWardenTaskName);
        BSTR bXml  = SysAllocString(xml.c_str());
        VARIANT vUser, vPwd, vSddl;
        VariantInit(&vUser);
        VariantInit(&vPwd);
        VariantInit(&vSddl);
        vSddl.vt = VT_BSTR;
        vSddl.bstrVal = SysAllocString(sddl.c_str());

        hr = folder->RegisterTask(bName, bXml, TASK_CREATE_OR_UPDATE,
                                  vUser, vPwd, TASK_LOGON_INTERACTIVE_TOKEN, vSddl, &task);

        VariantClear(&vSddl);
        SysFreeString(bXml);
        SysFreeString(bName);
    }

    if (task)    task->Release();
    if (folder)  folder->Release();
    if (root)    root->Release();
    if (service) service->Release();
    return hr;
}

// 移除排程工作與其資料夾。工作本來就不存在時視為成功。
HRESULT UnregisterElevatedTask() {
    ITaskService* service = nullptr;
    ITaskFolder*  root    = nullptr;
    ITaskFolder*  folder  = nullptr;

    HRESULT hr = ConnectTaskService(&service, &root);
    if (SUCCEEDED(hr)) {
        BSTR folderName = SysAllocString(kWardenTaskFolder);
        hr = root->GetFolder(folderName, &folder);
        if (SUCCEEDED(hr)) {
            BSTR taskName = SysAllocString(kWardenTaskName);
            HRESULT hrDel = folder->DeleteTask(taskName, 0);
            SysFreeString(taskName);
            if (FAILED(hrDel) && hrDel != HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND))
                hr = hrDel;
            folder->Release();
            folder = nullptr;
            // 資料夾清空後一併移除；還有別的工作在裡面就讓它失敗、不視為錯誤
            root->DeleteFolder(folderName, 0);
        } else if (hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ||
                   hr == HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)) {
            hr = S_OK;   // 沒註冊過 = 已經是目標狀態
        }
        SysFreeString(folderName);
    }

    if (root)    root->Release();
    if (service) service->Release();
    return hr;
}

// ── 安裝 / 移除 ─────────────────────────────────────────────────────────────

int DoInstall(const std::wstring& userSid, const std::wstring& source, const std::wstring& source2) {
    if (userSid.empty() || source.empty()) return 2;

    std::wstring dir = GetInstallDir();
    std::wstring dst = GetInstalledExePath();
    if (dir.empty() || dst.empty()) return 3;

    if (!EnsureSecureDir(dir)) return 4;

    // 先試套件內的正本；讀不到（WindowsApps 存取受限等）再退回主程式暫存的那份副本
    if (!CopyFileW(source.c_str(), dst.c_str(), FALSE)) {
        if (source2.empty() || !CopyFileW(source2.c_str(), dst.c_str(), FALSE))
            return 5;
    }

    HRESULT hr = RegisterElevatedTask(userSid, dst);
    if (FAILED(hr)) {
        // 工作沒註冊成功就不能留下執行檔——那個檔案同時是主程式判斷「已安裝」的依據
        DeleteFileW(dst.c_str());
        return 6;
    }
    return 0;
}

int DoUninstall() {
    HRESULT hr = UnregisterElevatedTask();

    std::wstring dst = GetInstalledExePath();
    if (!dst.empty()) DeleteFileW(dst.c_str());
    std::wstring dir = GetInstallDir();
    if (!dir.empty()) RemoveDirectoryW(dir.c_str());   // 目錄非空時失敗，不視為錯誤

    return FAILED(hr) ? 7 : 0;
}

} // namespace

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int) {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;

    HRESULT hrInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hrInit)) {
        LocalFree(argv);
        return 1;
    }

    int rc;
    if (HasFlag(argc, argv, L"--install")) {
        rc = DoInstall(GetArg(argc, argv, L"--sid"),
                       GetArg(argc, argv, L"--source"),
                       GetArg(argc, argv, L"--source2"));
    } else if (HasFlag(argc, argv, L"--uninstall")) {
        rc = DoUninstall();
    } else {
        rc = 2;   // 沒指定動作
    }

    CoUninitialize();
    LocalFree(argv);
    return rc;
}
