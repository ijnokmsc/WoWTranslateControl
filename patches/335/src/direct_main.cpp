// direct_main.cpp — WoWTranslateDirect.dll 入口：hook lua_gettop → 一次性捕获 L → 注册 WoWTranslate_*
//
// 机制对齐 GS DLL（决定版地址表，三源互证 2026-09-11）：
//   1. init 线程 sleep 3s 等客户端就绪 → 画像匹配（基址+签名）→ 内联 hook lua_gettop；
//   2. detour 一次性捕获第一参数 lua_State*（UI 主状态），注册后永久直通；
//   3. lua_pushcclosure + lua_setfield(_G) 注册 WoWTranslate_{Version,GetLastError,Status,Configure,
//      Translate,Poll,PendingCount,Diag}；
//   4. 编码边界：入站 GBK→UTF-8（进 HTTP），出站 UTF-8→GBK（lua_pushstring 裸字节）。
//
// 与 GS 的差异（本 DLL 的核心价值）：全程无宽字符→ANSI 转换，中文逐字 '?' 问题根治。
#include "wt_common.h"
#include "translator.h"
#include "driver_lua.h"
#include "json.hpp"   // third_party/json.hpp（translator.cpp 同款）
using json = nlohmann::json;

// ==================== 客户端画像（多版本适配架构，3.0）====================
//
// 适配单元 = ClientProfile：一个客户端构建的全部地址知识（基址、全局 lua_State
// 存放位置、FrameScript_Execute 错误处理器、Lua API 的 VA+序言签名表）。
// 运行时流程：init 线程枚举内置画像 → 逐个校验（模块基址 + 全部签名字节）→
// 首个全匹配者胜出；无一匹配则安全退出（不 hook），日志留下 exe 指纹
// （大小 + 前 64KB MD5 + PE 时间戳），新客户端照指纹补画像即可。
//
// 实测（tools_probe_multi.py，2026-09-14）：TriumvirateWoW 与 GrimfallWoW\Wotlk
// 两个 exe 文件哈希不同（大小相同、PE 时间戳相同），但下表 14/14 签名全匹配
// ——同一 3.3.5a 基座的不同魔改共用一个画像。
//
// sig = 序言字节签名（GS 日志 + exe 字节双源确认；其余函数至少验证 "55 8B" 标准序言）
// ⚠ getfield/settable/rawget 前 11 字节完全相同（55 8B EC 8B 45 0C 56 8B 75 08 8B CE），
//   必须用 17 字节（含 call rel32）才能区分——2026-09-12 IDA get_bytes 定案。
struct LuaAddr { DWORD va; const char* name; const char* sig; int sigLen; };

struct ClientProfile
{
    const char*   name;        // 画像名（进日志）
    DWORD         base;        // 期望主模块基址
    DWORD         globalLPtr;  // 存放全局 lua_State* 的数据地址（detour 门禁用）
    DWORD         errHandler;  // FrameScript_Execute 的 a3（客户端静态错误处理器）
    const char*   exeMd5Head;  // 客户端 exe 前 64KB 的 MD5 hex；null = 任意（签名字节相同的
                               //   魔改客户端用指纹区分——如 Grimfall 带函数指针白名单守卫）
    bool          caveStubs;   // true = 注册的 Lua C 函数走游戏 .text 代码洞跳板。
                               //   Grimfall 的 luaD_call 校验被调函数必须在 Wow.exe .text
                               //   内（ERROR #134 Invalid function pointer，IDA 0x86B5A0 实锤），
                               //   外部 DLL 的函数指针直接 fatal；跳板让闭包持有一个
                               //   白名单内的地址，实际执行 jmp 回本 DLL。
    const LuaAddr* addrs;
};

// 3.3.5a 基座地址表（grimfall/wotlk 画像共用；签名字节两客户端实测一致）
static const LuaAddr kAddrs335[14] = {
            { 0x84DBD0, "lua_gettop",       "\x55\x8B\xEC\x8B\x4D\x08\x8B\x41\x0C\x2B\x41\x10", 12 },
            { 0x84DBF0, "lua_settop",       "\x55\x8B\xEC", 3 },
            { 0x84E350, "lua_pushstring",   "\x55\x8B\xEC", 3 },
            { 0x84E400, "lua_pushcclosure", "\x55\x8B\xEC", 3 },
            { 0x84E600, "lua_rawget",       "\x55\x8B\xEC\x8B\x45\x0C\x56\x8B\x75\x08\x8B\xCE\xE8\xAF\xF3\xFF\xFF", 17 },
            { 0x84E670, "lua_getfield",     "\x55\x8B\xEC\x8B\x45\x0C\x56\x8B\x75\x08\x8B\xCE\xE8\x3F\xF3\xFF\xFF", 17 },
            // 真 settable = 0x84E8D0（wow_register 内部 call 目标，push (L, idx) 两参 cdecl）。
            //   PyWoW 表的 0x84E670 实为 lua_getfield（曾致 16-slot 泄漏崩溃）；0x84E600 是 lua_rawget。
            { 0x84E8D0, "lua_settable",     "\x55\x8B\xEC\x8B\x45\x0C\x56\x8B\x75\x08\x8B\xCE\xE8\xDF\xF0\xFF\xFF", 17 },
            { 0x84E0E0, "lua_tolstring",    "\x55\x8B\xEC", 3 },
            { 0x84DF60, "lua_isstring",     "\x55\x8B\xEC", 3 },
            { 0x84DF20, "lua_isnumber",     "\x55\x8B\xEC", 3 },
            { 0x84E030, "lua_tonumber",     "\x55\x8B\xEC", 3 },
            { 0x84E280, "lua_pushnil",      "\x55\x8B\xEC", 3 },
            { 0x84EC50, "lua_pcall",        "\x55\x8B\xEC", 3 },
            // FrameScript_Execute（/run 实现，IDA dump 0x819210）：__cdecl (code, len, errHandler)
            // 内部自带全局 L（profile.globalLPtr）、registry 错误处理器保存恢复、栈平衡。
            { 0x819210, "FrameScript_Execute", "\x55\x8B\xEC", 3 },
};

static const ClientProfile kProfiles[] = {
    {
        "grimfall-335a",        // GrimfallWoW：与通用 3.3.5a 签名相同，但带函数指针白名单
        0x400000,
        0xD3F78C,
        0xAC804C,
        "19B74B09224B875B1D848D2E44FFB511",   // exe 前 64KB MD5（v39 日志实测）
        true,                                 // 需要 .text 代码洞跳板
        kAddrs335,
    },
    {
        "wotlk-335a",           // 3.3.5a 基座通用画像（TriumvirateWoW 实测；指纹不匹配
                                // Grimfall 时落到这里）
        0x400000,
        0xD3F78C,               // dword_D3F78C：FrameScript_Execute 内部使用的全局 L
        0xAC804C,               // 客户端静态错误处理器（sub_510B30 同款传参）
        nullptr,                // 不限定指纹
        false,                  // 无白名单守卫，不需要跳板
        kAddrs335,
    },
};

static const ClientProfile* g_prof = nullptr;   // VerifyAddresses 胜出者（此后只读）
static char g_clientMd5Hex[33] = { 0 };          // 胜出画像的 exe 指纹（用于日志与画像匹配）

static DWORD VaOf(const char* name)
{
    if (!g_prof) return 0;
    for (int i = 0; i < 14; ++i)
        if (strcmp(g_prof->addrs[i].name, name) == 0)
            return g_prof->addrs[i].va;
    return 0;
}

// ==================== Lua API 函数指针（cdecl）====================

typedef struct lua_State lua_State;
typedef int (*lua_CFunction)(lua_State* L);

// ⚠ 伪索引定案（2026-09-12 IDA index2adr 0x84D9C0 反汇编，非反编译）：
//   -10000 → *(L+0x14)+0x68 = G(L)+0x68 = REGISTRY（注册表，标准 5.1！
//             此前会话误读 FrameScript_Execute 的 registry 错误处理器查找为「取全局」）
//   -10001 → 当前函数环境（ci->func 的 env，材质化到 L+0x58）
//   -10002 → L+0x48 内嵌 TValue = GLOBALS（gt 表就在 lua_State 里，非标准布局但标准索引值）
//   wow_register(0x8167E0) 用 settable(L,-3)——目标表由调用者压栈，从不用伪索引！
//   因此注册必须复刻官方模式：表压栈 + settable(-3)，不碰伪索引。
#define LUA_GLOBALSINDEX_L48 (-10002)   // index2adr → L+0x48

#define WT_NOINLINE __declspec(noinline)

typedef int         (*fn_lua_gettop)(lua_State*);
typedef void        (*fn_lua_pushstring)(lua_State*, const char*);
typedef void        (*fn_lua_pushcclosure)(lua_State*, lua_CFunction, int);
typedef void        (*fn_lua_settable)(lua_State*, int);   // 弹 key+value 写入表
typedef void        (*fn_lua_rawget)(lua_State*, int);     // 0x84E600：t@idx，key@top → 原位换 value（裸读，不走 __index）
typedef void        (*fn_lua_getfield)(lua_State*, int, const char*); // 0x84E670（裸读，压 1）
typedef void        (*fn_lua_settop)(lua_State*, int);
typedef const char* (*fn_lua_tolstring)(lua_State*, int, size_t*);
typedef int         (*fn_lua_isstring)(lua_State*, int);
typedef int         (*fn_lua_isnumber)(lua_State*, int);
typedef double      (*fn_lua_tonumber)(lua_State*, int);
typedef int         (*fn_FrameScript_Execute)(const char* code, int len, int errHandler); // 0x819210

static fn_lua_gettop        p_gettop;
static fn_lua_pushstring    p_pushstring;
static fn_lua_pushcclosure  p_pushcclosure;
static fn_lua_settable      p_settable;
static fn_lua_rawget        p_rawget;
static fn_lua_getfield      p_getfield;    // 0x84E670（裸读，用于自检/探针）
static fn_lua_settop        p_settop;
static fn_lua_tolstring     p_tolstring;
static fn_lua_isstring      p_isstring;
static fn_lua_isnumber      p_isnumber;
static fn_lua_tonumber      p_tonumber;
static fn_FrameScript_Execute p_Execute;   // 0x819210（主线程专用，见 TryInjectDriver）

// ==================== hook 状态 ====================

static void*  g_origBytes;        // 被覆盖的原始字节（6 字节）
static BYTE   g_trampoline[16];   // 原始 6 字节 + JMP 回原址+6
static void*  g_pTrampoline = g_trampoline;
static BYTE*  g_hookTarget = NULL; // 画像 lua_gettop 实际地址

static volatile LONG  g_hookInstalled = 0;
static volatile LONG  g_attemptCount = 0;

// ==================== 日志辅助 ====================

static std::string HexStr(const BYTE* p, int n)
{
    static const char* h = "0123456789ABCDEF";
    std::string s;
    for (int i = 0; i < n; ++i)
    {
        s += h[(p[i] >> 4) & 0xF];
        s += h[p[i] & 0xF];
        s += ' ';
    }
    return s;
}

// ==================== Lua C 函数实现 ====================
// 每个导出：外层裸函数（SEH 兜底，无 C++ 对象）→ 内层实现（C++ 对象安全展开）。

static int PushResult(lua_State* L, const std::string& gbkOrAscii)
{
    p_pushstring(L, gbkOrAscii.c_str());
    return 1;
}

// ---- Version: () → 版本串 ----
WT_NOINLINE static int L_Version_impl(lua_State* L)
{
    return PushResult(L, "WoWTranslateDirect 3.0.0 (Track B) by ijnokmsc");
}
WT_NOINLINE static int L_Version(lua_State* L)
{
    __try { return L_Version_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- GetLastError: () → 串 ----
WT_NOINLINE static int L_GetLastError_impl(lua_State* L)
{
    return PushResult(L, wt::Translator::Inst().GetLastErrorUtf8());   // UTF-8 通道，原样透传
}
WT_NOINLINE static int L_GetLastError(lua_State* L)
{
    __try { return L_GetLastError_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Status: () → JSON ----
WT_NOINLINE static int L_Status_impl(lua_State* L)
{
    return PushResult(L, wt::Translator::Inst().StatusJsonUtf8()); // ASCII/endpoint，无需转 GBK
}
WT_NOINLINE static int L_Status(lua_State* L)
{
    __try { return L_Status_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Configure: (json) → "ok" | "error|msg" ----
// v16：客户端是 UTF-8 通道（v15 \ddd 探针定案），原样透传不做 GBK 假设
//（/run 里用户敲的字符串、驱动注入的 JSON 都是 UTF-8 字节）。
WT_NOINLINE static int L_Configure_impl(lua_State* L)
{
    const char* raw = NULL;
    if (p_isstring(L, 1))
        raw = p_tolstring(L, 1, NULL);
    std::string json = raw ? raw : "";
    std::string result = wt::Translator::Inst().Configure(json);
    return PushResult(L, result);
}
WT_NOINLINE static int L_Configure(lua_State* L)
{
    __try { return L_Configure_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Translate: (text, from, to, requestId) → "ok" | "error|msg" ----
// v16：v15 定案客户端 = UTF-8 通道（AwesomeWotlk），入站原样透传。
// 旧 GbkToUtf8 会把 UTF-8 中文当 GBK 再编一次 → 乱码（ASCII 不受影响所以 v15 未暴露）。
WT_NOINLINE static int L_Translate_impl(lua_State* L)
{
    const char* raw = NULL;
    if (p_isstring(L, 1))
        raw = p_tolstring(L, 1, NULL);
    std::string text = raw ? raw : "";

    const char* from = p_isstring(L, 2) ? p_tolstring(L, 2, NULL) : "zh";
    const char* to   = p_isstring(L, 3) ? p_tolstring(L, 3, NULL) : "en";
    // requestId：驱动用 "out_N"（外发）/"N"（收发）字符串 id——非数字必须原样保留，
    // 旧逻辑 tonumber 会把 "out_1" 吞成 0，Poll 路由和并发都错乱
    std::string idStr;
    if (p_isstring(L, 4))
    {
        const char* s = p_tolstring(L, 4, NULL);
        if (s && *s) idStr = s;
    }
    if (idStr.empty())
    {
        double idNum = (p_isnumber(L, 4) || p_isstring(L, 4)) ? p_tonumber(L, 4) : 0.0;
        char idBuf[32];
        _snprintf(idBuf, sizeof(idBuf), "%.0f", idNum);
        idStr = idBuf;
    }

    // 入站编码判定：本客户端用户输入是 UTF-8，但服务器中转的其他玩家
    // 消息/名字是 GBK——非合法 UTF-8 的输入按 GBK→UTF-8 转换（v30）
    if (!text.empty() && !wt::IsUtf8(text))
    {
        text = wt::GbkToUtf8(text);
    }

    std::string err;
    if (!wt::Translator::Inst().Queue(idStr, text, from, to, err))
    {
        WT_LOG_ERROR("Translate queue failed: " + err);
        return PushResult(L, "error|" + err);
    }
    return PushResult(L, "ok");
}
WT_NOINLINE static int L_Translate(lua_State* L)
{
    __try { return L_Translate_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Poll: () → JSON {"id","translation","error"} 或 "" ----
// 译文 UTF-8 → GBK 后原样内嵌 JSON（Addon 的 JsonUnescape 按字节拷贝 → Lua 得 GBK 串）。
WT_NOINLINE static int L_Poll_impl(lua_State* L)
{
    std::string id, trans, err, orig;
    if (!wt::Translator::Inst().Poll(id, trans, err, orig))
        return PushResult(L, "");

    // ⚠ 客户端是 UTF-8 通道（TriumvirateWoW AwesomeWotlk 补丁，用户 \ddd 字节探针实测：
    //   UTF-8「测试」正常显示、GBK「测试」全为 ?）。控制台端点输出即 UTF-8，原样透传。
    // orig = 归一化后的原文（UTF-8），供驱动做中文回显去重。
    std::string out = "{\"id\":\"" + id + "\",\"translation\":\"" +
                      wt::JsonEscapeRaw(trans) +
                      "\",\"error\":\"" + wt::JsonEscapeRaw(err) +
                      "\",\"orig\":\"" + wt::JsonEscapeRaw(orig) + "\"}";
    return PushResult(L, out);
}
WT_NOINLINE static int L_Poll(lua_State* L)
{
    __try { return L_Poll_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- PendingCount: () → number ----
WT_NOINLINE static int L_PendingCount_impl(lua_State* L)
{
    lua_State* Lc = L; // 避免未用告警
    char buf[16];
    _snprintf(buf, sizeof(buf), "%d", wt::Translator::Inst().PendingCount());
    return PushResult(Lc, buf);
}
WT_NOINLINE static int L_PendingCount(lua_State* L)
{
    __try { return L_PendingCount_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Diag: ([msg]) → 诊断串；v17：驱动 chunk 用它向 C++ 回报注入结果 ----
// 约定：Lua 侧最后一步调用 WoWTranslate_Diag("WTC_DRIVER_OK" | "WTC_DRIVER_FAIL: 原因")。
static volatile LONG g_driverReported = 0;   // 见到 WTC_DRIVER_OK 置 1（TryInjectDriver 消费）

WT_NOINLINE static int L_Diag_impl(lua_State* L)
{
    if (p_isstring(L, 1))
    {
        const char* msg = p_tolstring(L, 1, NULL);
        std::string m = msg ? msg : "";
        if (m == "WTC_DRIVER_OK")
            InterlockedExchange(&g_driverReported, 1);
        WT_LOG_INFO(std::string("diag: ") + m);   // 失败原因（Lua err）直接进日志
        return PushResult(L, "ok");
    }
    return PushResult(L, "WoWTranslateDirect: hook=active, registered=1");
}
WT_NOINLINE static int L_Diag(lua_State* L)
{
    __try { return L_Diag_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ==================== 注册 ====================

struct RegEntry { const char* name; lua_CFunction fn; };
static RegEntry g_regs[] = {
    { "WoWTranslate_Version",      L_Version },
    { "WoWTranslate_GetLastError", L_GetLastError },
    { "WoWTranslate_Status",       L_Status },
    { "WoWTranslate_Configure",    L_Configure },
    { "WoWTranslate_Translate",    L_Translate },
    { "WoWTranslate_Poll",         L_Poll },
    { "WoWTranslate_PendingCount", L_PendingCount },
    { "WoWTranslate_Diag",         L_Diag },
};

// ---------- 代码洞跳板（Grimfall 函数指针白名单适配） ----------
// Grimfall 的 luaD_call 校验被调 C 函数必须位于 Wow.exe .text 内（IDA 0x86B5A0：
// 区间来自解析主模块 PE 的 .text 节）。跳板 = 洞内 7 字节 `mov eax,real; jmp eax`，
// Lua 闭包持有洞内地址（过白名单），真实实现在本 DLL。
static DWORD g_stubVa[8] = { 0 };                // 与 g_regs 一一对应的跳板 VA

static bool BuildCaveStubs()
{
    DWORD base = g_prof->base;
    const BYTE* pe = (const BYTE*)(uintptr_t)base;
    LONG e_lfanew = *(const LONG*)(pe + 0x3C);
    const BYTE* nth = pe + e_lfanew;
    WORD nsec = *(const WORD*)(nth + 6);
    WORD optsz = *(const WORD*)(nth + 20);
    const BYTE* sec = nth + 24 + optsz;
    DWORD tStart = 0, tEnd = 0;
    for (WORD i = 0; i < nsec; ++i)
    {
        const BYTE* s = sec + (DWORD)i * 40;
        if (memcmp(s, ".text", 6) == 0)
        {
            DWORD va = *(const DWORD*)(s + 12);
            tStart = base + va;
            tEnd = tStart + *(const DWORD*)(s + 16);   // + SizeOfRawData（节头偏移 16；s+20 是 PointerToRawData，v40 首版写错导致只扫了 1KB）
            break;
        }
    }
    if (!tStart)
    {
        WT_LOG_ERROR("cave stubs: .text section not found in game PE");
        return false;
    }

    int built = 0;
    DWORD runStart = 0, runLen = 0;
    BYTE runByte = 0xCC;
    for (DWORD p = tStart; p <= tEnd && built < 8; ++p)
    {
        BYTE b = (p < tEnd) ? *(const BYTE*)(uintptr_t)p : 0xFF;   // 结尾触发收尾
        bool pad = (b == 0xCC || b == 0x00);
        if (pad && runLen && b != runByte) pad = false;  // 换填充字节 → 结束当前 run
        if (pad)
        {
            if (runLen == 0) { runStart = p; runByte = b; }
            ++runLen;
            continue;
        }
        if (runLen >= 8)
        {
            DWORD slots = runLen / 8;
            for (DWORD j = 0; j < slots && built < 8; ++j)
            {
                DWORD cave = runStart + j * 8;
                DWORD real = (DWORD)(uintptr_t)g_regs[built].fn;
                BYTE stub[7] = { 0xB8 };                   // mov eax, imm32
                *(DWORD*)(stub + 1) = real;
                stub[5] = 0xFF; stub[6] = 0xE0;            // jmp eax
                DWORD oldProt = 0;
                if (!VirtualProtect((LPVOID)(uintptr_t)cave, 8, PAGE_EXECUTE_READWRITE, &oldProt))
                {
                    WT_LOG_ERROR("cave stubs: VirtualProtect failed");
                    return false;
                }
                memcpy((void*)(uintptr_t)cave, stub, 7);
                FlushInstructionCache(GetCurrentProcess(), (LPCVOID)(uintptr_t)cave, 8);
                VirtualProtect((LPVOID)(uintptr_t)cave, 8, oldProt, &oldProt);
                g_stubVa[built] = cave;
                ++built;
            }
        }
        runLen = 0;
    }
    if (built < 8)
    {
        WT_LOG_ERROR("cave stubs: not enough padding caves in game .text");
        return false;
    }
    for (int i = 0; i < 8; ++i)
        g_regs[i].fn = (lua_CFunction)(uintptr_t)g_stubVa[i];   // 注册表换成洞内地址
    char buf[96];
    _snprintf(buf, sizeof(buf), "cave stubs: 8 built (first @ 0x%08X in game .text)", g_stubVa[0]);
    WT_LOG_INFO(buf);
    return true;
}

// 按名字取当前画像中已验证的 VA；找不到返回 0（init 会拒绝全 0）


// 在 detour（Lua 主线程）内调用。v13 定案（2026-09-12，用户实测背书）：
// 结构探针（TValue 直读）→ 尝试 A（官方模式：gt TValue 压栈 + settable(-3)）→ 返回成功。
// 写入路径已被用户实测证实（/run print(type(WoWTranslate_Version)) == function，
// _G 无元表 nometa）；读回（rawget+TopTT）降级为诊断信息——v10-v12 三版假阴性
// （rc=3 / rc=5）全部出自读回端，getfield 读 print 也得 0。
// ⚠ __try 内禁止 std::string（C2712），所有结果存全局变量，由 TryRegister 在 __try 外打日志。
// 返回：0=写入+栈平衡 OK（读回值仅诊断），1=SEH，2=栈深异常，4=gt 不是表
static volatile LONG g_pG        = -1;   // *(L+0x14) = global_State
static volatile LONG g_pRegV     = -1;   // G+0x68 TValue.value（-10000 目标）
static volatile LONG g_pRegTT    = -1;
static volatile LONG g_pGtV      = -1;   // L+0x48 TValue.value（-10002 目标 = gt）
static volatile LONG g_pGtTT     = -1;
static volatile LONG g_ttA_ver   = -1;  // A 后 rawget 读回（诊断值）
static volatile LONG g_rcPath    = -1;  // 0=A

// 读 (top-1) 槽的 TValue.tt（3.3.5 布局：value 8B @0，tt @+8，warden shadow @+12）
static int TopTT(lua_State* L)
{
    return *(int*)(*(DWORD*)((BYTE*)L + 0xC) - 0x10 + 8);
}

// 模拟 /run 的 OP_GETGLOBAL：raw 读 gt，miss 后走 __index 表（raw 读链）。
// 栈自平衡到调用前深度。只能在 __try 内调用。
static int VisibilityTT(lua_State* L)
{
    BYTE* Lb = (BYTE*)L;
    int depth0 = ((fn_lua_gettop)g_pTrampoline)(L);
    DWORD top = *(DWORD*)(Lb + 0xC);
    memcpy((void*)top, Lb + 0x48, 16);               // 压 gt TValue 副本
    *(DWORD*)(Lb + 0xC) = top + 16;
    p_pushstring(L, "WoWTranslate_Version");
    p_rawget(L, -2);                                 // [E, v]
    int tt = TopTT(L);
    if ((tt & 0x1F) != 6)
    {
        p_pushstring(L, "__index");                  // [E, v, "__index"]
        p_rawget(L, -3);                             // t=E(-3) → slot := E.__index
        int ttIx = TopTT(L);
        if ((ttIx & 0x1F) == 5)                      // __index 是表 → 继续链
        {
            p_pushstring(L, "WoWTranslate_Version"); // [E, v, t2, name]
            p_rawget(L, -2);                         // t=t2(-2) → slot := t2[name]
            tt = TopTT(L);
        }
        else tt = 0;
    }
    while (((fn_lua_gettop)g_pTrampoline)(L) > depth0)
        p_settop(L, -2);                             // 逐个弹回到边界
    return tt;
}

static int TryRegisterCore(lua_State* L)
{
    int depth0 = ((fn_lua_gettop)g_pTrampoline)(L);
    __try
    {
        BYTE* Lb = (BYTE*)L;

        // ---------- 结构探针（TValue 直读——v12 实锤唯一可靠的读法；
        //            getfield 读回 print 也得 0 而 print 必在 _G，读路径弃用）----------
        DWORD G = *(DWORD*)(Lb + 0x14);
        g_pG = (LONG)G;
        g_pRegV = *(DWORD*)(G + 0x68);  g_pRegTT = *(int*)(G + 0x68 + 8);
        g_pGtV  = *(DWORD*)(Lb + 0x48); g_pGtTT  = *(int*)(Lb + 0x48 + 8);

        // ---------- 尝试 A：官方模式——gt TValue 压栈 + settable(-3) ----------
        if ((g_pGtTT & 0x1F) != 5)
            return 4;                                    // L+0x48 不是表 → 放弃 A

        DWORD top = *(DWORD*)(Lb + 0xC);
        memcpy((void*)top, Lb + 0x48, 16);               // 压 gt TValue 副本（含 shadow）
        *(DWORD*)(Lb + 0xC) = top + 16;

        for (int i = 0; i < (int)(sizeof(g_regs) / sizeof(g_regs[0])); ++i)
        {
            p_pushstring(L, g_regs[i].name);
            p_pushcclosure(L, g_regs[i].fn, 0);
            p_settable(L, -3);                           // t = gt @ -3（wow_register 同款）
            int depthNow = ((fn_lua_gettop)g_pTrampoline)(L);
            if (depthNow != depth0 + 1)
                return 2;
        }
        p_settop(L, -2);                                 // 弹出 gt 副本

        // ---------- 读回（仅记录，非致命）----------
        // v13 定案（用户实测背书）：写入 = 客户端 wow_register 官方同款三件套 + 栈平衡校验，
        // /run 已实测返回 function —— 写入端可信。v10-v12 三版假阴性全部出自读回端
        // （getfield 读 print 也得 0），故读回值降级为诊断信息，不再阻止注册完成。
        g_ttA_ver = VisibilityTT(L);
        g_rcPath = 0;
        return 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 1;
    }
}

WT_NOINLINE static int TryRegister(lua_State* L)
{
    int rc = TryRegisterCore(L);
    if (rc == 0)
        return rc;

    char buf[400];
    if (g_attemptCount == 1)
    {
        _snprintf(buf, sizeof(buf),
                  "probe G=0x%08X reg{v=0x%08X tt=%d} gt{v=0x%08X tt=%d}",
                  (unsigned)g_pG,
                  (unsigned)g_pRegV, (int)g_pRegTT,
                  (unsigned)g_pGtV, (int)g_pGtTT);
        WT_LOG_INFO(buf);
    }
    _snprintf(buf, sizeof(buf),
              "register failed rc=%d, attempt=%s",
              rc, std::to_string(g_attemptCount).c_str());
    WT_LOG_ERROR(buf);
    return rc;
}

// detour 辅助（在 Lua 主线程上跑，必须快、无分配、异常兜底）
//
// ⚠ 决定性设计（IDA 实锤 + 日志教训）：
// 1. 全局 L 门禁：profile.globalLPtr = 客户端全局 lua_State 存放处（FrameScript_Execute 用它跑 /run，
//    反编译铁证）。只在捕获 L == 该指针时注册 —— 客户端存在多个内部辅助状态
//    （日志实测 0x270083F8/0x3E8AB4F0 交替调 gettop），对它们注册纯属浪费且
//    会让 attempts 计数虚耗在错误目标上，/run 的 UI 状态反而轮不到。
// 2. 浅栈边界：depth<=2 才注册（GS 实测 stack top before: 1），避免在深度脚本
//    执行中插入 8 键触发 rehash 打断 next/pairs 迭代。
// 3. /reload 与重登：全局 L 指针变化 → 自动重置计数重走注册。
static lua_State* volatile g_globalL = NULL;      // 已注册的全局状态
static lua_State* volatile g_confirmedL = NULL;   // 已见 depth>0（真实脚本执行）的全局状态
static lua_State* volatile g_seenGlobalL = NULL;  // 上次观察到的 *(profile.globalLPtr)，用于状态切换日志
static volatile LONG g_registerDone = 0;

// ==================== v16：驱动 Lua 注入（全自治模式）====================

// 显示/外发配置（AutoConfigure 从 WoWTranslateDirect.json 解析；缺省 replace/[译]/off）
static std::string g_displayMode   = "replace";   // "replace" | "both"
static std::string g_displayPrefix = "[译]";      // UTF-8（客户端为 UTF-8 通道）
static std::string g_outgoingMode  = "off";       // "off" | "replace" | "both"
static int         g_outTimeout    = 25;        // 外发译文等待秒数

static std::string g_driverChunk;                 // 配置前缀 + 驱动 Lua，RebuildDriverChunk 组装
static HMODULE g_hSelfModule = NULL;              // 引擎自身句柄（DllMain 传入；日志/配置路径用）

static void ApplyDisplayKeys(const json& disp)
{
    if (disp.contains("displayMode") && disp["displayMode"].is_string())
        g_displayMode = disp["displayMode"].get<std::string>();
    if (disp.contains("displayPrefix") && disp["displayPrefix"].is_string())
        g_displayPrefix = disp["displayPrefix"].get<std::string>();
    if (g_displayMode != "replace" && g_displayMode != "both")
        g_displayMode = "replace";
    if (disp.contains("outgoingMode") && disp["outgoingMode"].is_string())
        g_outgoingMode = disp["outgoingMode"].get<std::string>();
    if (g_outgoingMode != "off" && g_outgoingMode != "replace" && g_outgoingMode != "both")
        g_outgoingMode = "off";
    if (disp.contains("outTimeout") && disp["outTimeout"].is_number())
        g_outTimeout = (std::max)(5, (std::min)(60, disp["outTimeout"].get<int>()));
    if (disp.contains("log") && disp["log"].is_boolean())
        wt::wtSetLogEnabled(disp["log"].get<bool>());
}

static void RebuildDriverChunk()
{
    // 用字符串数组拼接，避免行尾反斜杠被编辑器/工具链吞掉
    const char* part1 = "WTC={displayMode='";
    const char* part2 = "',prefix='";
    const char* part3 = "',outgoing='";
    const char* part4 = "',outTimeout=";
    const char* part5 = "}\n";
    g_driverChunk = std::string(part1) + wt::LuaEscape(g_displayMode) +
                    part2 + wt::LuaEscape(g_displayPrefix) +
                    part3 + wt::LuaEscape(g_outgoingMode) +
                    part4 + std::to_string(g_outTimeout) +
                    part5 +
                    wt::DriverLuaCode();
}

// /reload 时重读 WoWTranslateDirect.json 刷新显示/外发配置并重组驱动块——
// 用户在控制台改配置后打 /reload 即生效，无需重启客户端（v25）。
static void RefreshDriverConfig()
{
    char path[MAX_PATH];
    if (!g_hSelfModule || !GetModuleFileNameA(g_hSelfModule, path, MAX_PATH)) return;
    char* slash = strrchr(path, '\\');
    if (slash) *slash = 0;
    std::string cfgPath = std::string(path) + "\\WoWTranslateDirect.json";
    HANDLE h = CreateFileA(cfgPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    char buf[4096] = { 0 };
    DWORD rd = 0;
    ReadFile(h, buf, sizeof(buf) - 1, &rd, NULL);
    CloseHandle(h);
    if (rd == 0) return;
    try
    {
        json disp = json::parse(std::string(buf, rd));
        ApplyDisplayKeys(disp);
        RebuildDriverChunk();
        WT_LOG_INFO("config reloaded on /reload: mode=" + g_displayMode +
                    " outgoing=" + g_outgoingMode);
    }
    catch (...) { /* json 损坏时沿用当前配置 */ }
}

static volatile LONG g_driverDone = 0;            // 注入成功（或放弃）后置 1
static volatile LONG g_driverBusy = 0;            // 重入保护（FrameScript_Execute 内部会再触发 gettop）
static volatile LONG g_driverAttempts = 0;        // 重试计数
static volatile DWORD g_driverNextTick = 0;       // 下次允许尝试的 tick（500ms 节流）
static volatile LONG g_gateLogCount = 0;          // 门禁探针日志条数（最多 3 条）

#define DRIVER_MAX_ATTEMPTS 600                   // 500ms 节流下约 5 分钟，足够等 FrameXML

// 客户端 /run 处理器（sub_510B30）的同款传法：sub_819210(code, code, errHandler)
// —— a2 是 luaL_loadbuffer 的 chunk 名（客户端直接复用代码指针），传别的值时任何
// Lua 错误消息格式化都会把它当 char* 解引用 → AV（v16 崩溃根因之一）；
// a3 是客户端静态错误处理器指针，原样照抄（取自当前客户端画像 profile.errHandler）。

// SEH 执行体单独成函数（C2712：__try 所在函数禁止 std::string 临时量等需展开对象）
static int ExecuteDriverChunk()
{
    int rc = -1;
    __try
    {
        rc = p_Execute(g_driverChunk.c_str(),
                       (int)(intptr_t)g_driverChunk.c_str(),   // a2 = chunk 名（客户端同款：复用代码指针）
                       (int)(intptr_t)g_prof->errHandler);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        rc = -2;
    }
    return rc;
}

// 注册成功后，在 gettop detour（Lua 主线程）内等待 depth==0 干净边界注入。
// ⚠ 绝不能在 InitThread 等非主线程调 FrameScript_Execute —— Lua 状态非线程安全。
// v17：chunk 带就绪门禁（UI 未就绪时静默返回，绝不抛 Lua 错误），只有 chunk 调
// WoWTranslate_Diag("WTC_DRIVER_OK") 才算成功；否则 500ms 后重试直到上限。
static void TryInjectDriver(lua_State* L)
{
    if (g_driverDone) return;
    if (InterlockedCompareExchange(&g_driverBusy, 1, 0) != 0) return;

    DWORD now = GetTickCount();
    if (now < g_driverNextTick)                   // 节流：失败后至少间隔 500ms
    {
        InterlockedExchange(&g_driverBusy, 0);
        return;
    }
    InterlockedExchange(&g_driverNextTick, now + 500);

    int depth = ((fn_lua_gettop)g_pTrampoline)(L);
    if (depth > 2)                                // 与注册同窗口（0..2）。FrameScript_Execute
                                                  // 内部 pcall 建新调用帧，不触碰调用方栈；
                                                  // ⚠ 世界状态里 depth==0 几乎等不到（v17 实测
                                                  // 登录页能注入、进世界后永远等不到），故放宽
    {
        InterlockedExchange(&g_driverBusy, 0);
        return;
    }

    InterlockedExchange(&g_driverReported, 0);
    int rc = ExecuteDriverChunk();

    char buf[160];
    if (rc == -2)
    {
        // 只有 -2 是真 SEH；其他返回值是 FrameScript_Execute 的脚本嵌套计数
        //（depth≤2 注入通常嵌在客户端脚本内 → 返回 1，属正常，v18 曾误判停手）
        InterlockedExchange(&g_driverDone, 1);
        InterlockedExchange(&g_driverBusy, 0);
        _snprintf(buf, sizeof(buf), "driver inject SEH rc=-2 attempt=%d - stopped",
                  (int)g_driverAttempts + 1);
        WT_LOG_ERROR(buf);
        return;
    }
    if (g_driverReported)
    {
        InterlockedExchange(&g_driverDone, 1);
        InterlockedExchange(&g_driverBusy, 0);
        _snprintf(buf, sizeof(buf), "driver lua loaded OK (attempt=%d chunkLen=%d)",
                  (int)g_driverAttempts + 1, (int)g_driverChunk.size());
        WT_LOG_INFO(buf);
        return;
    }

    // 未就绪/未回报 → 保留 g_driverDone=0，下个空栈边界再试
    // v39：读取门禁探针全局（纯 Lua 写入的 WTC_GATE_INFO，见 driver_lua.h）。
    // 用注册同款的 getfield 裸读（Grimfall 实测安全），绝不调注册进 _G 的 API——
    // 该客户端校验嵌套 FrameScript_Execute 期间的 C 函数调用（v38 崩溃根因）。
    if (g_gateLogCount < 3 && p_getfield && p_tolstring && p_settop)
    {
        p_getfield(L, LUA_GLOBALSINDEX_L48, "WTC_GATE_INFO");
        const char* gate = p_tolstring(L, -1, NULL);
        if (gate && *gate)
        {
            ++g_gateLogCount;
            char gbuf[256];
            _snprintf(gbuf, sizeof(gbuf), "diag: WTC_GATE miss#%d %s",
                      (int)g_gateLogCount, gate);
            WT_LOG_INFO(gbuf);
        }
        p_settop(L, -2);   // 弹回探针值，恢复调用方栈
    }
    InterlockedExchange(&g_driverBusy, 0);
    LONG n = InterlockedIncrement(&g_driverAttempts);
    if (n == 1 || n % 40 == 0)
    {
        _snprintf(buf, sizeof(buf), "driver not ready yet, attempt=%d (see diag lines above)", (int)n);
        WT_LOG_INFO(buf);
    }
    if (n >= DRIVER_MAX_ATTEMPTS)
    {
        InterlockedExchange(&g_driverDone, 1);
        WT_LOG_ERROR("driver inject gave up after max attempts (FrameXML never became ready?)");
    }
}

static void OnGetTop(lua_State* L)
{
    if (!L) return;
    if (g_registerDone && L == g_globalL)
    {
        // 注册完成后的唯一入口：等待空栈边界注入驱动 Lua（一次性）
        if (!g_driverDone && g_driverChunk.size() > 0 && p_Execute)
            TryInjectDriver(L);
        return;
    }

    lua_State* cur = *(lua_State**)(uintptr_t)g_prof->globalLPtr;   // 全局 lua_State（画像）
    if (!cur || L != cur) return;                   // 内部辅助状态 → 一律不碰

    // 全局状态切换追踪（胶水态 → 世界态 → /reload 重建等）
    if (cur != g_seenGlobalL)
    {
        g_seenGlobalL = cur;
        char buf[80];
        _snprintf(buf, sizeof(buf), "global L switched: 0x%08X", (unsigned)(uintptr_t)cur);
        WT_LOG_INFO(buf);
        // /reload → 新 Lua 状态（旧驱动的 hook/帧随旧状态销毁）→ 重读配置 + 重注驱动
        InterlockedExchange(&g_driverDone, 0);
        InterlockedExchange(&g_driverAttempts, 0);
        RefreshDriverConfig();
    }

    // 调原函数（trampoline）拿真实栈深：top - base
    int depth = ((fn_lua_gettop)g_pTrampoline)(L);

    // 活性确认（GS 同款：call#1 stackUsed=0 被跳过，直到真实脚本执行才确认）：
    // 新全局 L 首次必须出现在 depth>0 才可信 —— depth=0 的干净边界可能是
    // 未就绪/临时状态（实测在 depth=0 注册后 /run 仍 nil）。
    if (L != g_confirmedL)
    {
        if (depth <= 0) return;
        g_confirmedL = L;
        InterlockedExchange(&g_attemptCount, 0);    // 新状态 → 重置计数
        char buf[96];
        _snprintf(buf, sizeof(buf),
                  "L confirmed alive: L=0x%08X depth=%d (waiting shallow stack)",
                  (unsigned)(uintptr_t)L, depth);
        WT_LOG_INFO(buf);
        if (depth > 2) return;                      // 确认了但深栈 → 等浅栈
    }
    else if (depth <= 0 || depth > 2)
    {
        return;                                     // 只在 1..2 浅栈窗口注册
    }

    LONG n = InterlockedIncrement(&g_attemptCount);
    if (n > 64)                                     // 该状态上 64 次没成功 → 放弃并不再刷屏
    {
        if (!g_registerDone)
        {
            WT_LOG_ERROR("give up registering this global L after 64 attempts");
            g_globalL = L;                          // 阻断重复尝试与日志刷屏
            InterlockedExchange(&g_registerDone, 1);
        }
        return;
    }

    int rc = TryRegister(L);
    if (rc == 0)
    {
        g_globalL = L;
        InterlockedExchange(&g_registerDone, 1);
        int depthAfter = ((fn_lua_gettop)g_pTrampoline)(L); // GS 同款栈恢复验证
        char buf[160];
        _snprintf(buf, sizeof(buf),
                  "WoWTranslate_* registered (L=0x%08X path=A:raw-gt depthBefore=%d depthAfter=%d "
                  "readback_tt=%d informational) verify=OK",
                  (unsigned)(uintptr_t)L, depth, depthAfter, (int)g_ttA_ver);
        WT_LOG_INFO(buf);
        // v18：注册完立即尝试注入（窗口内大概率可执行；UI 未就绪由 chunk 门禁 + 重试兜底）。
        // v17 教训：只等 depth==0 空栈边界，世界状态永远等不到 → 驱动进了世界后失效。
        TryInjectDriver(L);
    }
}

// ==================== detour（naked，保持所有寄存器/栈）====================

static void __declspec(naked) Detour_GetTop(void)
{
    __asm
    {
        mov eax, [esp+4]        ; L（cdecl 第一参数）
        push eax
        call OnGetTop           ; cdecl，参数由这里清理
        add esp, 4
        jmp [g_pTrampoline]     ; 跳回 trampoline（原 6 字节 + 回跳）
    }
}

// ==================== hook 安装 ====================

static bool InstallHook()
{
    // trampoline：原始 6 字节 + E9 回原址+6
    memcpy(g_trampoline, g_origBytes, 6);
    DWORD backAddr = (DWORD)g_hookTarget + 6;
    g_trampoline[6] = 0xE9;
    *(DWORD*)(g_trampoline + 7) = backAddr - ((DWORD)g_trampoline + 6 + 5);

    // trampoline 所在页改为可执行
    DWORD old = 0;
    if (!VirtualProtect(g_trampoline, sizeof(g_trampoline), PAGE_EXECUTE_READWRITE, &old))
    {
        WT_LOG_ERROR("VirtualProtect trampoline failed");
        return false;
    }

    // 目标处写 E9
    if (!VirtualProtect(g_hookTarget, 8, PAGE_EXECUTE_READWRITE, &old))
    {
        WT_LOG_ERROR("VirtualProtect target failed");
        return false;
    }
    g_hookTarget[0] = 0xE9;
    *(DWORD*)(g_hookTarget + 1) = (DWORD)&Detour_GetTop - ((DWORD)g_hookTarget + 5);
    DWORD tmp = 0;
    VirtualProtect(g_hookTarget, 8, old, &tmp);
    FlushInstructionCache(GetCurrentProcess(), g_hookTarget, 8);

    InterlockedExchange(&g_hookInstalled, 1);
    return true;
}

// ==================== init 线程 ====================

static bool VerifyAddresses()
{
    HMODULE base = GetModuleHandleW(NULL);
    if (!base) return false;
    DWORD actualBase = (DWORD)(uintptr_t)base;

    // exe 指纹：大小 + 前 64KB MD5 + 路径（新客户端补画像的依据）
    {
        char exePath[MAX_PATH] = { 0 };
        GetModuleFileNameA(NULL, exePath, MAX_PATH);
        unsigned long long fsize = 0;
        std::string md5head;
        HANDLE h = CreateFileA(exePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               NULL, OPEN_EXISTING, 0, NULL);
        if (h != INVALID_HANDLE_VALUE)
        {
            LARGE_INTEGER sz;
            if (GetFileSizeEx(h, &sz)) fsize = (unsigned long long)sz.QuadPart;
            static BYTE head[64 * 1024];
            DWORD rd = 0;
            if (ReadFile(h, head, sizeof(head), &rd, NULL) && rd > 0)
            {
                BYTE digest[16];
                if (wt::Md5(head, rd, digest))
                    md5head = HexStr(digest, 16);
            }
            CloseHandle(h);
        }
        char buf[400];
        _snprintf(buf, sizeof(buf),
                  "client fingerprint: size=%llu md5[64K]=%s exe=%s",
                  fsize, md5head.c_str(), exePath);
        WT_LOG_INFO(buf);
        // 归一为紧凑 hex 存全局（画像指纹匹配用）
        {
            const char* src = md5head.c_str();
            int o = 0;
            for (int i = 0; src[i] && o < 32; ++i)
                if (src[i] != ' ') g_clientMd5Hex[o++] = src[i];
            g_clientMd5Hex[32] = 0;
        }
    }

    char buf[128];
    _snprintf(buf, sizeof(buf), "module base=0x%08X", actualBase);
    WT_LOG_INFO(buf);

    // 枚举内置画像：基址一致 + 全部签名匹配者胜出
    for (int pi = 0; pi < (int)(sizeof(kProfiles) / sizeof(kProfiles[0])); ++pi)
    {
        const ClientProfile& prof = kProfiles[pi];
        if (actualBase != prof.base)
        {
            _snprintf(buf, sizeof(buf), "profile %s: base 0x%08X != module, skip",
                      prof.name, prof.base);
            WT_LOG_INFO(buf);
            continue;
        }
        if (prof.exeMd5Head && strcmp(g_clientMd5Hex, prof.exeMd5Head) != 0)
        {
            _snprintf(buf, sizeof(buf), "profile %s: fingerprint mismatch (got %s), skip",
                      prof.name, g_clientMd5Hex);
            WT_LOG_INFO(buf);
            continue;
        }

        bool all = true;
        for (int i = 0; i < 14 && all; ++i)
        {
            BYTE* p = (BYTE*)prof.addrs[i].va;
            if (IsBadReadPtr(p, prof.addrs[i].sigLen))
            {
                WT_LOG_ERROR(std::string("profile ") + prof.name + " unreadable: " +
                             prof.addrs[i].name);
                all = false;
                break;
            }
            if (memcmp(p, prof.addrs[i].sig, prof.addrs[i].sigLen) != 0)
            {
                WT_LOG_ERROR(std::string("profile ") + prof.name + " sig mismatch: " +
                             prof.addrs[i].name + " got " + HexStr(p, prof.addrs[i].sigLen));
                all = false;
                break;
            }
        }
        if (all)
        {
            g_prof = &prof;
            break;
        }
    }

    if (!g_prof)
    {
        WT_LOG_ERROR("no client profile matched - not hooking (see fingerprint above)");
        return false;
    }
    _snprintf(buf, sizeof(buf), "client profile matched: %s (all lua signatures verified)",
              g_prof->name);
    WT_LOG_INFO(buf);
    return true;
}


// 自动配置（v14）：优先读 DLL 同目录 WoWTranslateDirect.json；否则默认指向控制台伪装
// OpenAI 端点（架构定案：Track B 引擎 → 127.0.0.1:8080 → 控制台管线）。
// v13 日志实锤：/run WoWTranslate_Translate 报 "translator not configured"——引擎
// 从未拿到配置，配置入口只有 Lua Configure 与 SelfTest，游戏内无人调用。
static void AutoConfigure()
{
    char path[MAX_PATH];
    if (g_hSelfModule && GetModuleFileNameA(g_hSelfModule, path, MAX_PATH))
    {
        char* slash = strrchr(path, '\\');
        if (slash) *slash = 0;
        std::string cfgPath = std::string(path) + "\\WoWTranslateDirect.json";
        HANDLE h = CreateFileA(cfgPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                               NULL, OPEN_EXISTING, 0, NULL);
        if (h != INVALID_HANDLE_VALUE)
        {
            char buf[4096] = { 0 };
            DWORD rd = 0;
            ReadFile(h, buf, sizeof(buf) - 1, &rd, NULL);
            CloseHandle(h);
            if (rd > 0)
            {
                // v16：displayMode/displayPrefix 由本 DLL 消费（驱动 Lua 注入用），与
                // provider 配置解耦——json 解析失败或只写显示键时走默认端点配置。
                try
                {
                    json disp = json::parse(std::string(buf, rd));
                    ApplyDisplayKeys(disp);
                    WT_LOG_INFO("display config: mode=" + g_displayMode +
                                " outgoing=" + g_outgoingMode);
                }
                catch (...) {}

                std::string r = wt::Translator::Inst().Configure(std::string(buf, rd));
                WT_LOG_INFO("auto-config from file -> " + r);
                if (r.rfind("ok", 0) == 0) return;      // 文件配置成功
            }
        }
        WT_LOG_INFO("no usable WoWTranslateDirect.json beside DLL, applying default");
    }
    std::string r = wt::Translator::Inst().Configure(
        "{\"provider\":\"openai\",\"apiKey\":\"wtc\",\"model\":\"wtc\","
        "\"endpoint\":\"http://127.0.0.1:8080/v1/chat/completions\"}");
    WT_LOG_INFO("auto-config default console endpoint -> " + r);
}

static DWORD WINAPI InitThread(LPVOID)
{
    wt::wtSetSelfModule(g_hSelfModule);
    wt::LogInit();
    WT_LOG_INFO("WoWTranslateDirect 3.0.0 init (Track B direct engine) by ijnokmsc");
    AutoConfigure();

    // ⚠ GS 原版时序复刻："Init thread started, sleeping 3s... → Attempting hook..."
    // GS 的 init 线程睡 3 秒等客户端核心初始化完成后再装 hook —— 我们 v2-v4 删掉
    // 等待后 hook 覆盖了整个 Lua 初始化窗口， detour 参与了初始化全程，实测四种
    // 注册时机（stackUsed>0 / one-shot / 浅栈）全崩 → 问题在 hook 窗口而非注册时机。
    // Lua 初始化期间 gettop 调用极少（3s 内几乎无 Lua 活动），等 3 秒对捕获无影响。
    Sleep(3000);
    WT_LOG_INFO("sleep 3s done, attempting hook (GS-original timing)");

    if (!VerifyAddresses())
    {
        WT_LOG_ERROR("init aborted: address verification failed");
        return 1;
    }

    // 解析函数指针（VerifyAddresses 已选出画像 → VA 即实际地址）
    p_gettop       = (fn_lua_gettop)VaOf("lua_gettop");
    p_pushstring   = (fn_lua_pushstring)VaOf("lua_pushstring");
    p_pushcclosure = (fn_lua_pushcclosure)VaOf("lua_pushcclosure");
    p_settable     = (fn_lua_settable)VaOf("lua_settable");
    p_rawget       = (fn_lua_rawget)VaOf("lua_rawget");       // 裸读（不走 __index）
    p_getfield     = (fn_lua_getfield)VaOf("lua_getfield");   // 裸读压 1（自检/探针用）
    p_settop       = (fn_lua_settop)VaOf("lua_settop");
    p_tolstring    = (fn_lua_tolstring)VaOf("lua_tolstring");
    p_isstring     = (fn_lua_isstring)VaOf("lua_isstring");
    p_isnumber     = (fn_lua_isnumber)VaOf("lua_isnumber");
    p_tonumber     = (fn_lua_tonumber)VaOf("lua_tonumber");
    p_Execute      = (fn_FrameScript_Execute)VaOf("FrameScript_Execute");  // 主线程专用

    // Grimfall 类客户端：注册函数必须住在游戏 .text 内（函数指针白名单）
    if (g_prof->caveStubs && !BuildCaveStubs())
    {
        WT_LOG_ERROR("init aborted: cave stubs unavailable");
        return 1;
    }

    // v16 驱动块：配置前缀（WTC 全局表）+ 驱动 Lua，注册成功后主线程注入
    RebuildDriverChunk();

    // 保存 gettop 原 6 字节（gettop 签名前 6 字节，指令边界：push ebp / mov ebp,esp / mov ecx,[ebp+8]）
    g_hookTarget = (BYTE*)VaOf("lua_gettop");
    static BYTE orig[6];
    memcpy(orig, g_hookTarget, 6);
    g_origBytes = orig;
    WT_LOG_INFO("gettop original bytes: " + HexStr(orig, 6));

    if (!InstallHook())
    {
        WT_LOG_ERROR("init aborted: hook install failed");
        return 1;
    }
    WT_LOG_INFO("inline hook on lua_gettop installed, waiting for L capture");
    return 0;
}

// ==================== 自检导出（游戏外冒烟用）====================

extern "C" __declspec(dllexport) const char* __cdecl WoWTranslateDirect_SelfTest(void)
{
    static std::string result;
    result.clear();

    // GBK → UTF-8 → GBK 往返（"英雄模式" 的 GBK：D3 A2 D0 DB C4 A3 CAB BD）
    static const char gbk[] = { (char)0xD3, (char)0xA2, (char)0xD0, (char)0xDB,
                                (char)0xC4, (char)0xA3, (char)0xCA, (char)0xBD, 0 };
    std::string rt = wt::Utf8ToGbk(wt::GbkToUtf8(gbk));
    result += (rt == gbk) ? "gbk_roundtrip=OK" : "gbk_roundtrip=FAIL";

    // UTF-8 → GBK（"英雄模式" 的 UTF-8）
    std::string u8 = "\xE8\x8B\xB1\xE9\x9B\x84\xE6\xA8\xA1\xE5\xBC\x8F";
    std::string g = wt::Utf8ToGbk(u8);
    result += (memcmp(g.data(), gbk, 8) == 0) ? " utf8_to_gbk=OK" : " utf8_to_gbk=FAIL";

    // JSON 转义
    std::string esc = wt::JsonEscapeRaw(std::string("a\"b\\c\nd\x01"));
    result += (esc == "a\\\"b\\\\c\\nd\\u0001") ? " json_escape=OK" : " json_escape=FAIL";

    // 队列（未配置时应拒绝）
    std::string err;
    bool q = wt::Translator::Inst().Queue("1", "test", "en", "zh", err);
    result += (!q) ? " queue_guard=OK" : " queue_guard=FAIL";

    // ASCII 往返
    result += (wt::GbkToUtf8("hello") == "hello") ? " ascii=OK" : " ascii=FAIL";
    return result.c_str();
}

// ==================== DllMain ====================

// HTTP 回环探针（游戏外冒烟用）：配置 openai provider → 同步跑一次翻译 → 返回结果串。
// 用法：WoWTranslateDirect_Probe(endpoint)。
extern "C" __declspec(dllexport) const char* __cdecl WoWTranslateDirect_Probe(const char* endpoint)
{
    static std::string result;
    result.clear();
    if (!endpoint || !*endpoint)
        endpoint = "http://127.0.0.1:8080/v1/chat/completions";

    std::string cfg = "{\"provider\":\"openai\",\"apiKey\":\"probe\",\"model\":\"probe\",\"endpoint\":\"" +
                      std::string(endpoint) + "\"}";
    result += wt::Translator::Inst().Configure(cfg);

    std::string err;
    if (!wt::Translator::Inst().Queue("probe", "hello world", "en", "zh", err))
        return (result += " queue_fail:" + err).c_str();

    for (int i = 0; i < 350; ++i) // 最长等 35s
    {
        Sleep(100);
        std::string id, tr, er, og;
        if (wt::Translator::Inst().Poll(id, tr, er, og) && id == "probe")
        {
            result += " trans=" + tr + " err=" + er;
            return result.c_str();
        }
    }
    return (result += " timeout").c_str();
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/)
{
    switch (reason)
    {
        case DLL_PROCESS_ATTACH:
            g_hSelfModule = hModule;
            DisableThreadLibraryCalls(hModule);
            CreateThread(NULL, 0, InitThread, NULL, 0, NULL);
            break;
        case DLL_PROCESS_DETACH:
            wt::Translator::Inst().Shutdown();
            break;
        default: break;
    }
    return TRUE;
}
