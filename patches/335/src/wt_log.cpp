// wt_log.cpp — WoWTranslateDirect 文件日志实现
// 长持句柄追加写；初始化时超过 4MB 截断；CriticalSection 线程安全。
#include "wt_common.h"

namespace wt {

static CRITICAL_SECTION g_logCs;
static bool g_logReady = false;
static volatile LONG g_logEnabled = 1;   // 配置 "log": false 可关闭（wtSetLogEnabled）
static HANDLE g_logFile = NULL;
static HMODULE g_hSelf = NULL; // 引擎 DLL 自身模块句柄（DllMain 传入）

void wtSetSelfModule(HMODULE h) { g_hSelf = h; }

void wtSetLogEnabled(bool enabled)
{
    InterlockedExchange(&g_logEnabled, enabled ? 1 : 0);
}

static void GetLogPath(wchar_t (&path)[MAX_PATH])
{
    path[0] = 0;
    if (!g_hSelf) return;
    if (!GetModuleFileNameW(g_hSelf, path, MAX_PATH)) // 引擎 DLL 自身路径（游戏根目录）
        return;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) { path[0] = 0; return; }
    lstrcpyW(slash + 1, L"WoWTranslateDirect.log");
}

void LogInit()
{
    if (g_logReady) return;
    InitializeCriticalSection(&g_logCs);
    g_logReady = true;

    wchar_t path[MAX_PATH];
    GetLogPath(path);
    if (!path[0]) return;

    // 初始截断检查
    HANDLE hProbe = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hProbe != INVALID_HANDLE_VALUE)
    {
        LARGE_INTEGER sz;
        if (GetFileSizeEx(hProbe, &sz) && sz.QuadPart > 4 * 1024 * 1024)
            DeleteFileW(path);
        CloseHandle(hProbe);
    }

}

void LogWrite(const char* level, const std::string& msg)
{
    if (!g_logEnabled) return;                 // 配置关闭：不初始化、不写、不创建文件
    if (!g_logReady) LogInit();
    if (!g_logReady) return;
    if (g_logFile == INVALID_HANDLE_VALUE || g_logFile == NULL)
    {
        wchar_t path[MAX_PATH];
        GetLogPath(path);
        if (!path[0]) { g_logReady = false; return; }
        g_logFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
                                OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (g_logFile == INVALID_HANDLE_VALUE) { g_logReady = false; return; }
    }

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[2048];
    int n = _snprintf(line, sizeof(line) - 2,
                      "[%02u:%02u:%02u.%03u][%s] %s\r\n",
                      st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                      level, msg.c_str());
    if (n <= 0) return;
    line[n] = 0;

    EnterCriticalSection(&g_logCs);
    DWORD written = 0;
    WriteFile(g_logFile, line, (DWORD)n, &written, NULL);
    LeaveCriticalSection(&g_logCs);
}

} // namespace wt
