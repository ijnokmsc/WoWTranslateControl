// selftest_host.cpp — WoWTranslateDirect.dll 游戏外自检宿主（32 位）
// 验证编码往返 / JSON 转义 / 队列守卫。加载后 DllMain 的 init 线程会因宿主
// 地址空间无 3.3.5 Lua 函数而安全中止（VerifyAddresses 失败即 abort）。
#include <windows.h>
#include <cstdio>

int main(int argc, char** argv)
{
    HMODULE h = LoadLibraryA("WoWTranslateDirect.dll");
    if (!h)
    {
        printf("LoadLibrary failed, err=%lu\n", GetLastError());
        return 1;
    }
    typedef const char* (__cdecl* Fn)();
    Fn f = (Fn)GetProcAddress(h, "WoWTranslateDirect_SelfTest");
    if (!f)
    {
        printf("GetProcAddress failed, err=%lu\n", GetLastError());
        return 1;
    }
    printf("SelfTest: %s\n", f());

    // 可选：HTTP 回环探针（传入 endpoint 参数时执行）
    if (argc >= 2 && argv[1] && argv[1][0])
    {
        typedef const char* (__cdecl* Probe)(const char*);
        Probe p = (Probe)GetProcAddress(h, "WoWTranslateDirect_Probe");
        if (p)
            printf("Probe: %s\n", p(argv[1]));
        else
            printf("Probe export missing\n");
    }

    // 等 init 线程走完安全中止路径（3s sleep + 校验），避免卸载竞态
    Sleep(5000);
    printf("HOST OK\n");
    return 0; // 不 FreeLibrary：init 线程生命周期由进程退出自然回收
}
