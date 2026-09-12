// direct_main.cpp — WoWTranslateDirect.dll 入口：hook lua_gettop → 一次性捕获 L → 注册 WoWTranslate_*
//
// 机制对齐 GS DLL（决定版地址表，三源互证 2026-09-11）：
//   1. init 线程 sleep 3s 等客户端就绪 → 字节码序言校验 → 内联 hook lua_gettop(0x84DBD0)；
//   2. detour 一次性捕获第一参数 lua_State*（UI 主状态），注册后永久直通；
//   3. lua_pushcclosure + lua_setfield(_G) 注册 WoWTranslate_{Version,GetLastError,Status,Configure,
//      Translate,Poll,PendingCount,Diag}；
//   4. 编码边界：入站 GBK→UTF-8（进 HTTP），出站 UTF-8→GBK（lua_pushstring 裸字节）。
//
// 与 GS 的差异（本 DLL 的核心价值）：全程无宽字符→ANSI 转换，中文逐字 '?' 问题根治。
#include "wt_common.h"
#include "translator.h"

// ==================== 3.3.5a 决定版地址表（默认基址 0x400000）====================

static const DWORD DEFAULT_BASE = 0x400000;

struct LuaAddr { DWORD va; const char* name; const char* sig; int sigLen; };

// sig = 序言字节签名（GS 日志 + exe 字节双源确认；其余函数至少验证 "55 8B" 标准序言）
// ⚠ getfield/settable/rawget 前 11 字节完全相同（55 8B EC 8B 45 0C 56 8B 75 08 8B CE），
//   必须用 17 字节（含 call rel32）才能区分——2026-09-12 IDA get_bytes 定案。
static LuaAddr g_addrs[] = {
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
};

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

// ==================== hook 状态 ====================

static void*  g_origBytes;        // 被覆盖的原始字节（6 字节）
static BYTE   g_trampoline[16];   // 原始 6 字节 + JMP 回原址+6
static void*  g_pTrampoline = g_trampoline;
static BYTE*  g_hookTarget = NULL; // 0x84DBD0 实际地址

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
    return PushResult(L, "WoWTranslateDirect 1.0 (Track B)");
}
WT_NOINLINE static int L_Version(lua_State* L)
{
    __try { return L_Version_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- GetLastError: () → 串 ----
WT_NOINLINE static int L_GetLastError_impl(lua_State* L)
{
    return PushResult(L, wt::Utf8ToGbk(wt::Translator::Inst().GetLastErrorUtf8()));
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
WT_NOINLINE static int L_Configure_impl(lua_State* L)
{
    const char* raw = NULL;
    if (p_isstring(L, 1))
        raw = p_tolstring(L, 1, NULL);
    std::string jsonGbk = raw ? raw : "";
    std::string result = wt::Translator::Inst().Configure(wt::GbkToUtf8(jsonGbk));
    return PushResult(L, result);
}
WT_NOINLINE static int L_Configure(lua_State* L)
{
    __try { return L_Configure_impl(L); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// ---- Translate: (text, from, to, requestId) → "ok" | "error|msg" ----
WT_NOINLINE static int L_Translate_impl(lua_State* L)
{
    const char* raw = NULL;
    if (p_isstring(L, 1))
        raw = p_tolstring(L, 1, NULL);
    std::string textGbk = raw ? raw : "";

    const char* from = p_isstring(L, 2) ? p_tolstring(L, 2, NULL) : "zh";
    const char* to   = p_isstring(L, 3) ? p_tolstring(L, 3, NULL) : "en";
    double idNum = (p_isnumber(L, 4) || p_isstring(L, 4)) ? p_tonumber(L, 4) : 0.0;

    char idBuf[32];
    _snprintf(idBuf, sizeof(idBuf), "%.0f", idNum);

    std::string err;
    if (!wt::Translator::Inst().Queue(idBuf, wt::GbkToUtf8(textGbk), from, to, err))
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
    std::string id, trans, err;
    if (!wt::Translator::Inst().Poll(id, trans, err))
        return PushResult(L, "");

    std::string out = "{\"id\":\"" + id + "\",\"translation\":\"" +
                      wt::JsonEscapeRaw(wt::Utf8ToGbk(trans)) +
                      "\",\"error\":\"" + wt::JsonEscapeRaw(wt::Utf8ToGbk(err)) + "\"}";
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

// ---- Diag: () → 诊断串（GS DLL 有同名函数，Addon 可选用）----
WT_NOINLINE static int L_Diag_impl(lua_State* L)
{
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

// 在 detour（Lua 主线程）内调用。v12 设计（2026-09-12，IDA 反汇编定案）：
// 结构探针 → 尝试 A（官方模式：gt TValue 压栈 + settable(-3)）→ 尝试 B（写 gt.__index 函数库表）。
// 可见性自检 = 模拟 /run 的 OP_GETGLOBAL：raw 读 gt，miss 后走 __index 链。
// ⚠ __try 内禁止 std::string（C2712），所有结果存全局变量，由 TryRegister 在 __try 外打日志。
// 返回：0=A 成功，6=B 成功，1=SEH，2=栈深异常，4=gt 不是表，5=无 __index 表，7=A 后可见性失败且 B 不适用
static volatile LONG g_pG        = -1;   // *(L+0x14) = global_State
static volatile LONG g_pRegV     = -1;   // G+0x68 TValue.value（-10000 目标）
static volatile LONG g_pRegTT    = -1;
static volatile LONG g_pGtV      = -1;   // L+0x48 TValue.value（-10002 目标 = gt）
static volatile LONG g_pGtTT     = -1;
static volatile LONG g_ttRegPrint = -1;  // getfield(-10000,"print")
static volatile LONG g_ttGtPrint  = -1;  // getfield(-10002,"print")
static volatile LONG g_ttEnvPrint = -1;  // getfield(-10001,"print")
static volatile LONG g_ttRegVer   = -1;  // getfield(-10000,"WoWTranslate_Version")（v11 遗留写回读）
static volatile LONG g_ttGtVer0   = -1;  // 注册前 getfield(-10002,"WoWTranslate_Version")
static volatile LONG g_ttA_ver    = -1;  // A 后 raw 读 gt
static volatile LONG g_ttB_vis    = -1;  // B 后 OP_GETGLOBAL 模拟
static volatile LONG g_rcPath     = -1;  // 0=A，1=B

// 读 (top-1) 槽的 TValue.tt（3.3.5 布局：value 8B @0，tt @+8，warden shadow @+12）
static int TopTT(lua_State* L)
{
    return *(int*)(*(DWORD*)((BYTE*)L + 0xC) - 0x10 + 8);
}

// getfield(idx, name) → 读 tt → 弹回。只能在 __try 内调用。
static int ProbeTT(lua_State* L, int idx, const char* name)
{
    p_getfield(L, idx, name);
    int tt = TopTT(L);
    p_settop(L, -2);
    return tt;
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

        // ---------- 结构探针（一次运行回答所有布局问题）----------
        DWORD G = *(DWORD*)(Lb + 0x14);
        g_pG = (LONG)G;
        g_pRegV = *(DWORD*)(G + 0x68);  g_pRegTT = *(int*)(G + 0x68 + 8);
        g_pGtV  = *(DWORD*)(Lb + 0x48); g_pGtTT  = *(int*)(Lb + 0x48 + 8);
        if ((g_pRegTT & 0x1F) == 5)
        {
            g_ttRegPrint = ProbeTT(L, -10000, "print");
            g_ttRegVer   = ProbeTT(L, -10000, "WoWTranslate_Version");
        }
        if ((g_pGtTT & 0x1F) == 5)
        {
            g_ttGtPrint = ProbeTT(L, -10002, "print");
            g_ttGtVer0  = ProbeTT(L, -10002, "WoWTranslate_Version");
        }
        g_ttEnvPrint = ProbeTT(L, -10001, "print");

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

        g_ttA_ver = ProbeTT(L, LUA_GLOBALSINDEX_L48, "WoWTranslate_Version");
        if ((g_ttA_ver & 0x1F) == 6)
        {
            g_rcPath = 0;
            return 0;                                    // raw 写 gt 成功，/run raw 命中
        }

        // ---------- 尝试 B：写入 gt.__index（客户端函数库表 t2）----------
        top = *(DWORD*)(Lb + 0xC);
        memcpy((void*)top, Lb + 0x48, 16);               // [gt]
        *(DWORD*)(Lb + 0xC) = top + 16;
        p_pushstring(L, "__index");                      // [gt, "__index"]
        p_rawget(L, -2);                                 // [gt, t2?]（裸读）
        int ttT2 = TopTT(L);
        if ((ttT2 & 0x1F) != 5)
        {
            p_settop(L, -2);                             // 弹 t2 槽
            p_settop(L, -2);                             // 弹 gt
            g_rcPath = 2;
            return 5;                                    // gt 无 __index 表
        }
        for (int i = 0; i < (int)(sizeof(g_regs) / sizeof(g_regs[0])); ++i)
        {
            p_pushstring(L, g_regs[i].name);             // [gt,t2,name]
            p_pushcclosure(L, g_regs[i].fn, 0);          // [gt,t2,name,fn]
            p_settable(L, -3);                           // t2[name]=fn → [gt,t2]
            int depthNow = ((fn_lua_gettop)g_pTrampoline)(L);
            if (depthNow != depth0 + 2)
                return 2;
        }
        p_settop(L, -3);                                 // 弹 t2+gt
        g_ttB_vis = VisibilityTT(L);
        if ((g_ttB_vis & 0x1F) == 6)
        {
            g_rcPath = 1;
            return 6;                                    // 经 __index 链可见
        }
        return 7;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 1;
    }
}

WT_NOINLINE static int TryRegister(lua_State* L)
{
    int rc = TryRegisterCore(L);
    if (rc == 0 || rc == 6)
        return rc;

    char buf[400];
    if (g_attemptCount == 1)
    {
        _snprintf(buf, sizeof(buf),
                  "probe G=0x%08X reg{v=0x%08X tt=%d} gt{v=0x%08X tt=%d} "
                  "print[reg=%d gt=%d env=%d] ver[reg=%d gt0=%d]",
                  (unsigned)g_pG,
                  (unsigned)g_pRegV, (int)g_pRegTT,
                  (unsigned)g_pGtV, (int)g_pGtTT,
                  (int)g_ttRegPrint, (int)g_ttGtPrint, (int)g_ttEnvPrint,
                  (int)g_ttRegVer, (int)g_ttGtVer0);
        WT_LOG_INFO(buf);
    }
    _snprintf(buf, sizeof(buf),
              "register failed rc=%d path=%d ttA=%d ttBvis=%d, attempt=%s",
              rc, (int)g_rcPath, (int)g_ttA_ver, (int)g_ttB_vis,
              std::to_string(g_attemptCount).c_str());
    WT_LOG_ERROR(buf);
    return rc;
}

// detour 辅助（在 Lua 主线程上跑，必须快、无分配、异常兜底）
//
// ⚠ 决定性设计（IDA 实锤 + 日志教训）：
// 1. 全局 L 门禁：0xD3F78C = 客户端全局 lua_State（FrameScript_Execute 用它跑 /run，
//    反编译铁证）。只在捕获 L == 该指针时注册 —— 客户端存在多个内部辅助状态
//    （日志实测 0x270083F8/0x3E8AB4F0 交替调 gettop），对它们注册纯属浪费且
//    会让 attempts 计数虚耗在错误目标上，/run 的 UI 状态反而轮不到。
// 2. 浅栈边界：depth<=2 才注册（GS 实测 stack top before: 1），避免在深度脚本
//    执行中插入 8 键触发 rehash 打断 next/pairs 迭代。
// 3. /reload 与重登：全局 L 指针变化 → 自动重置计数重走注册。
static lua_State* volatile g_globalL = NULL;      // 已注册的全局状态
static lua_State* volatile g_confirmedL = NULL;   // 已见 depth>0（真实脚本执行）的全局状态
static lua_State* volatile g_seenGlobalL = NULL;  // 上次观察到的 *(0xD3F78C)，用于状态切换日志
static volatile LONG g_registerDone = 0;

static void OnGetTop(lua_State* L)
{
    if (!L) return;
    if (g_registerDone && L == g_globalL) return;   // 完成+同全局 L → 单比较直通

    lua_State* cur = *(lua_State**)0xD3F78C;        // 客户端全局 lua_State
    if (!cur || L != cur) return;                   // 内部辅助状态 → 一律不碰

    // 全局状态切换追踪（胶水态 → 世界态 → /reload 重建等）
    if (cur != g_seenGlobalL)
    {
        g_seenGlobalL = cur;
        char buf[80];
        _snprintf(buf, sizeof(buf), "global L switched: 0x%08X", (unsigned)(uintptr_t)cur);
        WT_LOG_INFO(buf);
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
    if (rc == 0 || rc == 6)
    {
        g_globalL = L;
        InterlockedExchange(&g_registerDone, 1);
        int depthAfter = ((fn_lua_gettop)g_pTrampoline)(L); // GS 同款栈恢复验证
        char buf[128];
        _snprintf(buf, sizeof(buf),
                  "WoWTranslate_* registered (L=0x%08X path=%s depthBefore=%d depthAfter=%d) verify=OK",
                  (unsigned)(uintptr_t)L, rc == 0 ? "A:raw-gt" : "B:__index-t2",
                  depth, depthAfter);
        WT_LOG_INFO(buf);
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

    char buf[128];
    _snprintf(buf, sizeof(buf), "module base=0x%08X (default 0x%08X)", actualBase, DEFAULT_BASE);
    WT_LOG_INFO(buf);
    if (actualBase != DEFAULT_BASE)
    {
        WT_LOG_ERROR("unexpected module base - address table needs rebasing, aborting");
        return false;
    }

    for (int i = 0; i < (int)(sizeof(g_addrs) / sizeof(g_addrs[0])); ++i)
    {
        BYTE* p = (BYTE*)g_addrs[i].va;
        if (IsBadReadPtr(p, g_addrs[i].sigLen))
        {
            WT_LOG_ERROR(std::string("unreadable: ") + g_addrs[i].name);
            return false;
        }
        if (memcmp(p, g_addrs[i].sig, g_addrs[i].sigLen) != 0)
        {
            WT_LOG_ERROR(std::string("sig mismatch: ") + g_addrs[i].name +
                         " got " + HexStr(p, g_addrs[i].sigLen));
            return false;
        }
    }
    WT_LOG_INFO("all lua function signatures verified");
    return true;
}

static HMODULE g_hSelfModule = NULL;

static DWORD WINAPI InitThread(LPVOID)
{
    wt::wtSetSelfModule(g_hSelfModule);
    wt::LogInit();
    WT_LOG_INFO("WoWTranslateDirect 1.0 init (Track B direct engine)");

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

    // 解析函数指针（已验证基址 == 默认 → VA 即实际地址）
    p_gettop       = (fn_lua_gettop)0x84DBD0;
    p_pushstring   = (fn_lua_pushstring)0x84E350;
    p_pushcclosure = (fn_lua_pushcclosure)0x84E400;
    p_settable     = (fn_lua_settable)0x84E8D0;
    p_rawget       = (fn_lua_rawget)0x84E600;     // 裸读（不走 __index）
    p_getfield     = (fn_lua_getfield)0x84E670;   // 裸读压 1（自检/探针用）
    p_settop       = (fn_lua_settop)0x84DBF0;
    p_tolstring    = (fn_lua_tolstring)0x84E0E0;
    p_isstring     = (fn_lua_isstring)0x84DF60;
    p_isnumber     = (fn_lua_isnumber)0x84DF20;
    p_tonumber     = (fn_lua_tonumber)0x84E030;

    // 保存 gettop 原 6 字节（gettop 签名前 6 字节，指令边界：push ebp / mov ebp,esp / mov ecx,[ebp+8]）
    g_hookTarget = (BYTE*)0x84DBD0;
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
        std::string id, tr, er;
        if (wt::Translator::Inst().Poll(id, tr, er) && id == "probe")
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
