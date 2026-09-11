// wt_common.h — WoWTranslateDirect 共享工具：日志 + GBK/UTF-8 编码 + JSON 字符串转义
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

namespace wt {

// ==================== 日志 ====================
// 日志文件写在 DLL 自身目录（游戏根目录）：WoWTranslateDirect.log
// 追加写 + 4MB 截断；线程安全（CriticalSection）。

void LogInit();
void LogWrite(const char* level, const std::string& msg);
void wtSetSelfModule(HMODULE h); // DllMain 传入引擎自身句柄（日志落 DLL 所在目录）

#define WT_LOG_INFO(msg)  ::wt::LogWrite("INFO ", (msg))
#define WT_LOG_WARN(msg)  ::wt::LogWrite("WARN ", (msg))
#define WT_LOG_ERROR(msg) ::wt::LogWrite("ERROR", (msg))

// ==================== GBK / UTF-8 转换 ====================
// zhCN 3.3.5a 客户端 Lua 字符串 = GBK 字节。DLL 边界做两次确定转换：
//   出站：GBK → UTF-8（进 HTTP/JSON）
//   入站：UTF-8 → GBK（lua_pushstring 推裸 GBK 字节）

inline std::string GbkToUtf8(const std::string& s)
{
    if (s.empty()) return s;
    int wlen = MultiByteToWideChar(936, 0, s.data(), (int)s.size(), NULL, 0);
    if (wlen <= 0) return s; // 转换失败原样透传
    std::wstring w(wlen, L'\0');
    MultiByteToWideChar(936, 0, s.data(), (int)s.size(), &w[0], wlen);

    int ulen = WideCharToMultiByte(CP_UTF8, 0, w.data(), wlen, NULL, 0, NULL, NULL);
    if (ulen <= 0) return s;
    std::string u(ulen, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), wlen, &u[0], ulen, NULL, NULL);
    return u;
}

inline std::string Utf8ToGbk(const std::string& s)
{
    if (s.empty()) return s;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), NULL, 0);
    if (wlen <= 0) return s; // 不是合法 UTF-8（或为空）→ 原样透传
    std::wstring w(wlen, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], wlen);

    int glen = WideCharToMultiByte(936, 0, w.data(), wlen, NULL, 0, NULL, NULL);
    if (glen <= 0) return s;
    std::string g(glen, '\0');
    WideCharToMultiByte(936, 0, w.data(), wlen, &g[0], glen, NULL, NULL);
    return g;
}

// ==================== JSON 字符串转义 ====================
// 用于 Poll 结果 JSON：译文已是 GBK 裸字节，只需转义控制字符/引号/反斜杠，
// 高位字节原样内嵌（Addon 的 JsonUnescape 对无 \u 的字节按原样拷贝 → Lua 里即为 GBK 串）。

inline std::string JsonEscapeRaw(const std::string& s)
{
    static const char* hex = "0123456789ABCDEF";
    std::string out;
    out.reserve(s.size() + 16);
    for (size_t i = 0; i < s.size(); ++i)
    {
        unsigned char c = (unsigned char)s[i];
        switch (c)
        {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (c < 0x20)
                {
                    out += "\\u00";
                    out += hex[(c >> 4) & 0xF];
                    out += hex[c & 0xF];
                }
                else
                {
                    out += (char)c;
                }
        }
    }
    return out;
}

} // namespace wt
