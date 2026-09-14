// translator.h — WoWTranslateDirect 翻译引擎（winhttp 三 provider + 异步队列 + 缓存）
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <deque>
#include <map>
#include <mutex>
#include <thread>
#include <atomic>

namespace wt {

class Translator
{
public:
    static Translator& Inst();

    // 配置（JSON 与 GS Addon BuildConfigJson 契约一致，UTF-8 输入）
    // 返回 "ok" 或 "error|消息"
    std::string Configure(const std::string& utf8Json);

    // 入队异步翻译（text 为 UTF-8）。成功返回 true，失败 err 填原因。
    bool Queue(const std::string& id, const std::string& textUtf8,
               const std::string& from, const std::string& to, std::string& err);

    // 弹出一条完成结果（UTF-8 译文）
    bool Poll(std::string& idOut, std::string& transOut, std::string& errOut,
               std::string& origOut);

    int  PendingCount();
    std::string StatusJsonUtf8();   // {"provider":..,"configured":..,"ready":..,"endpoint":..,"lastHttpStatus":N}
    std::string GetLastErrorUtf8();
    void Shutdown();

private:
    Translator() = default;
    ~Translator() = default;

    enum class Provider { None, OpenAI, GoogleFree, Custom };

    struct ParsedUrl
    {
        bool valid = false;
        bool secure = false;
        std::string host;
        int port = 0;
        std::string pathAndQuery;
    };

    struct Job
    {
        std::string id;
        std::string text;   // UTF-8
        std::string from;
        std::string to;
    };

    struct Result
    {
        std::string id;
        std::string translation; // UTF-8
        std::string error;
        std::string orig;        // 归一化后的原文（UTF-8），供回显去重
    };

    static ParsedUrl ParseUrl(const std::string& url);
    std::string HttpOnce(const ParsedUrl& url, bool isGet, const std::string& body,
                         const std::string& authHeaderLine);
    std::string TranslateOne(const Job& job, std::string& error);

    std::string TrOpenAI(const Job& job, std::string& error);
    std::string TrGoogleFree(const Job& job, std::string& error);
    std::string TrCustom(const Job& job, std::string& error);

    std::string CacheGet(const std::string& key);
    void CachePut(const std::string& key, const std::string& value);
    std::string CacheKey(const std::string& text, const std::string& from, const std::string& to);
    std::string UrlEncode(const std::string& s);

    void WorkerLoop();

    std::mutex m_cfgMx;
    Provider m_provider = Provider::None;
    std::string m_endpoint;          // openai/custom
    std::string m_apiKey;
    std::string m_model;
    double m_temperature = 0.0;
    std::string m_systemPrompt;      // openai 可选
    std::string m_authHeader, m_authScheme, m_requestTemplate, m_responsePath; // custom
    std::string m_lastError;
    DWORD m_lastHttpStatus = 0;

    void* m_hSession = nullptr; // HINTERNET（只能跨线程用于同步请求创建连接，winhttp 会话句柄线程安全）

    std::mutex m_qMx;
    std::deque<Job> m_queue;
    std::deque<Result> m_results;

    std::mutex m_cacheMx;
    std::map<std::string, std::pair<std::string, DWORD>> m_cache; // key → {value, tick}

    std::thread m_worker;
    std::atomic<bool> m_running{false};
    bool m_started = false;
};

} // namespace wt
