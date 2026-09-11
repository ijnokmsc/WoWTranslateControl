// dinput8_proxy.cpp — dinput8 转发代理（Track B 加载链入口）
//
// Wow.exe 对 DINPUT8.dll 的导入表只有一项：DirectInput8Create（按名导入）。
// 本代理：
//   1. 加载系统真实 dinput8（C:\Windows\SysWOW64\dinput8.dll），naked stub 转发 5 个标准导出；
//   2. 起线程读自身目录下的 dlls.txt，逐行 LoadLibraryW（WoWTranslateDirect.dll）。
//
// 依赖零：/MT 静态 CRT，不依赖 GS 的 MinGW 运行库。
// 纯 Win32 C，不使用 CRT 初始化敏感操作；dlls.txt 读取在独立线程做（避开 loader lock）。

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// ============ 真实 dinput8 函数指针 ============

static HMODULE g_realDinput = NULL;

// ============ naked 转发 stub（jmp [ptr]，不扰动寄存器/栈）============

static FARPROC g_pDirectInput8Create;
static FARPROC g_pDllCanUnloadNow;
static FARPROC g_pDllGetClassObject;
static FARPROC g_pDllRegisterServer;
static FARPROC g_pDllUnregisterServer;

__declspec(naked) void __stdcall Stub_DirectInput8Create(void)
{
    __asm { jmp [g_pDirectInput8Create] }
}

__declspec(naked) void __stdcall Stub_DllCanUnloadNow(void)
{
    __asm { jmp [g_pDllCanUnloadNow] }
}

__declspec(naked) void __stdcall Stub_DllGetClassObject(void)
{
    __asm { jmp [g_pDllGetClassObject] }
}

__declspec(naked) void __stdcall Stub_DllRegisterServer(void)
{
    __asm { jmp [g_pDllRegisterServer] }
}

__declspec(naked) void __stdcall Stub_DllUnregisterServer(void)
{
    __asm { jmp [g_pDllUnregisterServer] }
}

// ============ dlls.txt 引擎加载线程 ============

static DWORD WINAPI LoaderThreadReal(LPVOID param)
{
    HMODULE hSelf = (HMODULE)param;
    wchar_t selfPath[MAX_PATH];
    if (!GetModuleFileNameW(hSelf, selfPath, MAX_PATH))
        return 0;

    // 截取目录
    wchar_t* slash = wcsrchr(selfPath, L'\\');
    if (!slash)
        return 0;
    *slash = 0;

    wchar_t listPath[MAX_PATH];
    lstrcpyW(listPath, selfPath);
    lstrcatW(listPath, L"\\dlls.txt");

    HANDLE hFile = CreateFileW(listPath, GENERIC_READ, FILE_SHARE_READ, NULL,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE)
        return 0;

    // 读全文（dlls.txt 很小，一次读完）
    char buf[4096];
    DWORD read = 0;
    if (!ReadFile(hFile, buf, sizeof(buf) - 1, &read, NULL))
    {
        CloseHandle(hFile);
        return 0;
    }
    CloseHandle(hFile);
    buf[read] = 0;

    // 逐行解析（支持 \n 与 \r\n；# 注释；空行跳过）；ANSI 行 → wchar
    char line[MAX_PATH];
    int lineLen = 0;
    for (DWORD i = 0; i <= read; ++i)
    {
        char c = buf[i];
        if (c == '\n' || c == '\r' || i == read)
        {
            line[lineLen] = 0;
            if (lineLen > 0 && line[0] != '#')
            {
                wchar_t wline[MAX_PATH];
                if (MultiByteToWideChar(CP_UTF8, 0, line, -1, wline, MAX_PATH) > 0 ||
                    MultiByteToWideChar(CP_ACP, 0, line, -1, wline, MAX_PATH) > 0)
                {
                    // 相对名（如 "WoWTranslateDirect.dll"）→ LoadLibrary 默认按应用目录搜索
                    LoadLibraryW(wline);
                }
            }
            lineLen = 0;
            if (i == read)
                break;
        }
        else if (lineLen < MAX_PATH - 1)
        {
            line[lineLen++] = c;
        }
    }
    return 0;
}

// ============ DllMain ============

BOOL WINAPI DllMain(HINSTANCE hSelf, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hSelf);

        // 1. 加载系统真实 dinput8 并解析导出
        //    绝对路径：避免按名加载再次命中本代理（应用目录优先）造成循环。
        g_realDinput = LoadLibraryW(L"C:\\Windows\\SysWOW64\\dinput8.dll");
        if (g_realDinput)
        {
            g_pDirectInput8Create   = GetProcAddress(g_realDinput, "DirectInput8Create");
            g_pDllCanUnloadNow      = GetProcAddress(g_realDinput, "DllCanUnloadNow");
            g_pDllGetClassObject    = GetProcAddress(g_realDinput, "DllGetClassObject");
            g_pDllRegisterServer    = GetProcAddress(g_realDinput, "DllRegisterServer");
            g_pDllUnregisterServer  = GetProcAddress(g_realDinput, "DllUnregisterServer");
        }

        // 2. 起线程加载 dlls.txt 列出的引擎 DLL（不能在 DllMain 里同步 LoadLibrary 之外的重活）
        CreateThread(NULL, 0, LoaderThreadReal, (LPVOID)hSelf, 0, NULL);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // 游戏进程通常直接退出；真实 dinput8 不做显式 FreeLibrary（进程生命周期内持有）
    }
    return TRUE;
}
