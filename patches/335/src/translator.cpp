// translator.cpp — WoWTranslateDirect 翻译引擎实现
//
// Provider：
//   openai      → OpenAI 兼容 /v1/chat/completions（含本地控制台 http://127.0.0.1:8080，支持 http+https）
//   google_free → translate.googleapis.com/translate_a/single (gtx, GET)
//   custom      → 请求模板 + responsePath
// 编码契约：本层全部 UTF-8；GBK 转换在 Lua 边界（direct_main.cpp）。
#include "translator.h"
#include "wt_common.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winhttp.h>
#include <algorithm>
#include <cctype>
#include <sstream>

#ifdef _MSC_VER
#pragma warning(push, 0)
#endif
#include "json.hpp"
#ifdef _MSC_VER
#pragma warning(pop)
#endif

#pragma comment(lib, "winhttp.lib")

using json = nlohmann::json;

namespace wt {

static const DWORD CACHE_TTL_MS   = 10 * 60 * 1000;
static const size_t CACHE_MAX     = 512;
static const DWORD HTTP_TIMEOUTS[4] = { 5000, 5000, 15000, 28000 }; // resolve/connect/send/receive

static std::string JDump(const json& j)
{
    // error_handler_t::replace：聊天文本可能含非法 UTF-8（服务器 255 字节截断半字符），
    // 裸 dump() 会 throw → worker 线程 std::terminate → 游戏进程死亡。
    return j.dump(-1, ' ', false, json::error_handler_t::replace);
}

static std::string JsonErrMessage(const json& parsed, DWORD httpStatus)
{
    try
    {
        if (parsed.contains("error"))
        {
            const json& e = parsed["error"];
            if (e.is_string()) return e.get<std::string>();
            if (e.is_object() && e.contains("message") && e["message"].is_string())
                return e["message"].get<std::string>();
        }
        if (parsed.contains("message") && parsed["message"].is_string())
            return parsed["message"].get<std::string>();
    }
    catch (...) {}
    if (httpStatus > 0) return "HTTP " + std::to_string(httpStatus);
    return "provider error";
}

// 从 JSON 按点路径取字符串（"choices[0].message.content"）
static bool ExtractJsonPath(const json& root, const std::string& path, std::string& out)
{
    if (path.empty()) return false;
    const json* cur = &root;
    size_t pos = 0;
    while (pos < path.length())
    {
        size_t dot = path.find('.', pos);
        std::string seg = (dot == std::string::npos) ? path.substr(pos) : path.substr(pos, dot - pos);
        pos = (dot == std::string::npos) ? path.length() : dot + 1;
        if (seg.empty()) return false;

        size_t brk = seg.find('[');
        std::string key = (brk == std::string::npos) ? seg : seg.substr(0, brk);
        if (!key.empty())
        {
            if (!cur->is_object() || !cur->contains(key)) return false;
            cur = &(*cur)[key];
        }
        while (brk != std::string::npos)
        {
            size_t close = seg.find(']', brk + 1);
            if (close == std::string::npos) return false;
            int idx = atoi(seg.substr(brk + 1, close - brk - 1).c_str());
            if (!cur->is_array() || idx < 0 || (size_t)idx >= cur->size()) return false;
            cur = &(*cur)[(size_t)idx];
            brk = seg.find('[', close + 1);
        }
    }
    if (cur->is_string()) out = cur->get<std::string>();
    else if (cur->is_number() || cur->is_boolean()) out = cur->dump();
    else return false;
    return !out.empty();
}

Translator& Translator::Inst()
{
    static Translator inst;
    return inst;
}

// ==================== 配置 ====================

std::string Translator::Configure(const std::string& utf8Json)
{
    json cfg;
    try
    {
        cfg = json::parse(utf8Json);
    }
    catch (const std::exception& e)
    {
        WT_LOG_ERROR(std::string("Configure parse failed: ") + e.what());
        return "error|invalid config json";
    }

    std::string provider;
    try
    {
        if (cfg.contains("provider") && cfg["provider"].is_string())
            provider = cfg["provider"].get<std::string>();
    }
    catch (...) {}

    Provider np = Provider::None;
    std::string ne = m_endpoint, nk = m_apiKey, nm = m_model;
    double nt = m_temperature;
    std::string nsp = m_systemPrompt;
    std::string nah = "Authorization", nas = "Bearer", nrt = "", nrp = "translation";

    if (provider == "openai")
    {
        np = Provider::OpenAI;
        ne = cfg.value("endpoint", ne.empty() ? "https://api.openai.com/v1/chat/completions" : ne);
        nk = cfg.value("apiKey", nk);
        nm = cfg.value("model", nm.empty() ? "gpt-4.1-mini" : nm);
        nsp = cfg.value("systemPrompt", nsp);
        if (nm.empty()) nm = "gpt-4.1-mini";
    }
    else if (provider == "google_free")
    {
        np = Provider::GoogleFree;
    }
    else if (provider == "custom")
    {
        np = Provider::Custom;
        ne = cfg.value("endpoint", ne);
        nk = cfg.value("apiKey", nk);
        nah = cfg.value("authHeader", nah);
        nas = cfg.value("authScheme", nas);
        nrt = cfg.value("requestTemplate", nrt);
        nrp = cfg.value("responsePath", nrp);
        if (nrp.empty()) nrp = "translation";
    }
    else
    {
        return "error|unknown provider: " + provider;
    }

    ParsedUrl pu = ParseUrl(ne);
    if ((np == Provider::OpenAI || np == Provider::Custom) && !pu.valid)
        return "error|endpoint must be a valid http(s) URL";

    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        m_provider = np;
        m_endpoint = ne;
        m_apiKey = nk;
        m_model = nm;
        m_temperature = nt;
        m_systemPrompt = nsp;
        m_authHeader = nah;
        m_authScheme = nas;
        m_requestTemplate = nrt;
        m_responsePath = nrp;
        m_lastError.clear();
        m_lastHttpStatus = 0;
    }

    WT_LOG_INFO(std::string("Configured provider=") + provider + " endpoint=" + ne);

    // 启动 worker
    if (!m_started)
    {
        m_running = true;
        try
        {
            m_worker = std::thread(&Translator::WorkerLoop, this);
            m_started = true;
        }
        catch (const std::exception& e)
        {
            m_running = false;
            WT_LOG_ERROR(std::string("worker start failed: ") + e.what());
            return "error|worker thread start failed";
        }
    }

    return "ok";
}

// ==================== 队列 ====================

bool Translator::Queue(const std::string& id, const std::string& textUtf8,
                       const std::string& from, const std::string& to, std::string& err)
{
    Provider p;
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        p = m_provider;
        err = m_lastError;
    }
    if (p == Provider::None || !m_started)
    {
        err = "translator not configured";
        return false;
    }
    if (textUtf8.empty())
    {
        err = "empty text";
        return false;
    }

    Job job;
    job.id = id;
    job.text = textUtf8;
    job.from = from.empty() ? "auto" : from;
    job.to = to.empty() ? "zh" : to;

    std::lock_guard<std::mutex> lk(m_qMx);
    if (m_queue.size() > 64)
    {
        err = "queue full";
        return false;
    }
    m_queue.push_back(std::move(job));
    return true;
}

bool Translator::Poll(std::string& idOut, std::string& transOut, std::string& errOut,
                      std::string& origOut)
{
    std::lock_guard<std::mutex> lk(m_qMx);
    if (m_results.empty()) return false;
    Result r = std::move(m_results.front());
    m_results.pop_front();
    idOut = std::move(r.id);
    transOut = std::move(r.translation);
    errOut = std::move(r.error);
    origOut = std::move(r.orig);
    return true;
}

int Translator::PendingCount()
{
    std::lock_guard<std::mutex> lk(m_qMx);
    return (int)m_queue.size();
}

std::string Translator::GetLastErrorUtf8()
{
    std::lock_guard<std::mutex> lk(m_cfgMx);
    return m_lastError;
}

std::string Translator::StatusJsonUtf8()
{
    std::lock_guard<std::mutex> lk(m_cfgMx);
    const char* pname = "none";
    bool configured = false;
    std::string endpoint;
    switch (m_provider)
    {
        case Provider::OpenAI:
            pname = "openai";
            configured = !m_endpoint.empty() && !m_apiKey.empty();
            endpoint = m_endpoint;
            break;
        case Provider::GoogleFree:
            pname = "google_free";
            configured = true;
            endpoint = "https://translate.googleapis.com/translate_a/single";
            break;
        case Provider::Custom:
            pname = "custom";
            configured = !m_endpoint.empty();
            endpoint = m_endpoint;
            break;
        default: break;
    }
    json st;
    st["provider"] = pname;
    st["configured"] = configured;
    st["ready"] = m_started;
    st["endpoint"] = endpoint;
    st["lastHttpStatus"] = m_lastHttpStatus;
    return JDump(st);
}

void Translator::Shutdown()
{
    if (m_running.exchange(false))
    {
        if (m_worker.joinable())
            m_worker.join();
        m_started = false;
    }
    if (m_hSession)
    {
        WinHttpCloseHandle((HINTERNET)m_hSession);
        m_hSession = nullptr;
    }
}

// ==================== worker ====================

void Translator::WorkerLoop()
{
    WT_LOG_INFO("worker thread started");
    for (;;)
    {
        Job job;
        bool has = false;
        {
            std::lock_guard<std::mutex> lk(m_qMx);
            if (!m_queue.empty())
            {
                job = std::move(m_queue.front());
                m_queue.pop_front();
                has = true;
            }
        }

        if (has)
        {
            Result r;
            r.id = job.id;
            r.orig = job.text;
            try
            {
                // DLL 层缓存优先（省一次本地回环往返）
                std::string ck = CacheKey(job.text, job.from, job.to);
                std::string hit = CacheGet(ck);
                if (!hit.empty())
                {
                    r.translation = hit;
                }
                else
                {
                    std::string error;
                    std::string tr = TranslateOne(job, error);
                    if (!error.empty())
                    {
                        // 瞬态失败（网络抖动/上游瞬时故障）单次重试，重试仍败才回传错误
                        Sleep(400);
                        std::string error2;
                        std::string tr2 = TranslateOne(job, error2);
                        WT_LOG_WARN("translate retry: " + (error2.empty() ? "succeeded" : "failed again: " + error2));
                        if (error2.empty())
                        {
                            tr = tr2;
                            error.clear();
                        }
                        else
                        {
                            error = error2;
                        }
                    }
                    if (error.empty())
                    {
                        r.translation = tr;
                        CachePut(ck, tr);
                    }
                    else
                    {
                        r.error = error;
                    }
                }
            }
            catch (const std::exception& e)
            {
                r.error = std::string("internal error: ") + e.what();
                WT_LOG_ERROR("worker exception: " + r.error);
            }
            catch (...)
            {
                r.error = "internal error: unknown exception";
                WT_LOG_ERROR("worker unknown exception");
            }

            {
                std::lock_guard<std::mutex> lk(m_qMx);
                m_results.push_back(std::move(r));
                // 结果积压防护：Addon 30s 超时后旧结果无人认领
                while (m_results.size() > 128)
                    m_results.pop_front();
            }
        }
        else
        {
            Sleep(30);
            if (!m_running) break;
        }
    }
    WT_LOG_INFO("worker thread stopped");
}

// ==================== 翻译路由 ====================

std::string Translator::TranslateOne(const Job& job, std::string& error)
{
    Provider p;
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        p = m_provider;
    }

    std::string result;
    bool ok = false;
    switch (p)
    {
        case Provider::OpenAI:      result = TrOpenAI(job, error); ok = error.empty(); break;
        case Provider::GoogleFree:  result = TrGoogleFree(job, error); ok = error.empty(); break;
        case Provider::Custom:      result = TrCustom(job, error); ok = error.empty(); break;
        default: error = "translator not configured"; break;
    }
    if (ok)
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        m_lastError.clear();
    }
    else
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        m_lastError = error;
    }
    return result;
}

std::string Translator::TrOpenAI(const Job& job, std::string& error)
{
    std::string endpoint, apiKey, model, sysPrompt;
    double temperature = 0.0;
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        endpoint = m_endpoint;
        apiKey = m_apiKey;
        model = m_model;
        sysPrompt = m_systemPrompt;
        temperature = m_temperature;
    }

    if (apiKey.empty())
    {
        error = "OpenAI-compatible API key is not configured";
        return "";
    }

    json body;
    body["model"] = model.empty() ? "gpt-4.1-mini" : model;
    body["temperature"] = temperature;
    json msgs = json::array();
    if (!sysPrompt.empty())
        msgs.push_back({ {"role", "system"}, {"content", sysPrompt} });
    // 控制台取「最后一条 user 消息」为待译文本 → user content 必须是纯原文，
    // 不掺 Source/Target 前缀（会污染 Filter 规则与缓存键）。
    msgs.push_back({ {"role", "user"}, {"content", job.text} });
    body["messages"] = msgs;

    ParsedUrl url = ParseUrl(endpoint);
    if (!url.valid)
    {
        error = "endpoint must be a valid http(s) URL";
        return "";
    }

    std::string authLine = "Authorization: Bearer " + apiKey;
    std::string resp = HttpOnce(url, false, JDump(body), authLine);
    if (resp.empty())
    {
        DWORD st;
        {
            std::lock_guard<std::mutex> lk(m_cfgMx);
            st = m_lastHttpStatus;
        }
        error = (st == 0) ? "network error" : ("HTTP " + std::to_string(st));
        return "";
    }

    try
    {
        json parsed = json::parse(resp);
        DWORD st;
        {
            std::lock_guard<std::mutex> lk(m_cfgMx);
            st = m_lastHttpStatus;
        }
        if (st >= 400 || parsed.contains("error"))
        {
            error = JsonErrMessage(parsed, st);
            return "";
        }
        std::string out;
        if (!ExtractJsonPath(parsed, "choices[0].message.content", out))
        {
            error = "response missing choices[0].message.content";
            return "";
        }
        // 去首尾空白
        size_t b = out.find_first_not_of(" \t\r\n");
        size_t e = out.find_last_not_of(" \t\r\n");
        return (b == std::string::npos) ? "" : out.substr(b, e - b + 1);
    }
    catch (const std::exception& e)
    {
        error = std::string("parse response failed: ") + e.what();
        return "";
    }
}

std::string Translator::TrGoogleFree(const Job& job, std::string& error)
{
    std::string sl = (job.from.empty() || job.from == "auto") ? "auto" : job.from;
    std::string url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=" +
                      UrlEncode(sl) + "&tl=" + UrlEncode(job.to) + "&q=" + UrlEncode(job.text);

    ParsedUrl pu = ParseUrl(url);
    if (!pu.valid)
    {
        error = "bad google_free url";
        return "";
    }
    std::string resp = HttpOnce(pu, true, "", "");
    if (resp.empty())
    {
        error = "network error";
        return "";
    }

    // gtx 响应：[[["译","原",...],...],...] → 逐段拼接 [0][i][0]
    try
    {
        json parsed = json::parse(resp);
        std::string out;
        if (parsed.is_array() && !parsed.empty() && parsed[0].is_array())
        {
            for (auto& seg : parsed[0])
            {
                if (seg.is_array() && !seg.empty() && seg[0].is_string())
                    out += seg[0].get<std::string>();
            }
        }
        if (out.empty())
        {
            error = "google_free parse failed";
            return "";
        }
        return out;
    }
    catch (const std::exception& e)
    {
        error = std::string("google_free parse failed: ") + e.what();
        return "";
    }
}

std::string Translator::TrCustom(const Job& job, std::string& error)
{
    std::string endpoint, apiKey, authHeader, authScheme, requestTemplate, responsePath;
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        endpoint = m_endpoint;
        apiKey = m_apiKey;
        authHeader = m_authHeader;
        authScheme = m_authScheme;
        requestTemplate = m_requestTemplate;
        responsePath = m_responsePath;
    }

    if (requestTemplate.empty())
        requestTemplate = "{\"text\":\"{text}\",\"source\":\"{source}\",\"target\":\"{target}\"}";

    ParsedUrl url = ParseUrl(endpoint);
    if (!url.valid)
    {
        error = "endpoint must be a valid http(s) URL";
        return "";
    }

    json jtext = job.text, jfrom = job.from, jto = job.to;
    std::string body = requestTemplate;
    const std::string escText = jtext.dump(); // 带引号的合法 JSON 字符串字面量
    const std::string escFrom = jfrom.dump();
    const std::string escTo = jto.dump();
    auto ReplaceAll = [](std::string& s, const std::string& f, const std::string& t) {
        size_t pos = 0;
        while ((pos = s.find(f, pos)) != std::string::npos)
        {
            s.replace(pos, f.length(), t);
            pos += t.length();
        }
    };
    ReplaceAll(body, "{text}", escText.substr(1, escText.size() - 2));
    ReplaceAll(body, "{source}", escFrom.substr(1, escFrom.size() - 2));
    ReplaceAll(body, "{target}", escTo.substr(1, escTo.size() - 2));

    std::string authLine;
    if (!apiKey.empty() && !authHeader.empty())
    {
        std::string value = (authScheme.empty() || authScheme == "none")
            ? apiKey : (authScheme + " " + apiKey);
        authLine = authHeader + ": " + value;
    }

    std::string resp = HttpOnce(url, false, body, authLine);
    if (resp.empty())
    {
        error = "network error";
        return "";
    }

    try
    {
        json parsed = json::parse(resp);
        DWORD st;
        {
            std::lock_guard<std::mutex> lk(m_cfgMx);
            st = m_lastHttpStatus;
        }
        if (st >= 400 || parsed.contains("error"))
        {
            error = JsonErrMessage(parsed, st);
            return "";
        }
        std::string out;
        if (!ExtractJsonPath(parsed, responsePath.empty() ? "translation" : responsePath, out))
        {
            error = "response missing path: " + responsePath;
            return "";
        }
        return out;
    }
    catch (const std::exception& e)
    {
        error = std::string("custom parse failed: ") + e.what();
        return "";
    }
}

// ==================== HTTP ====================

Translator::ParsedUrl Translator::ParseUrl(const std::string& url)
{
    ParsedUrl p;
    size_t schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos) return p;
    std::string scheme = url.substr(0, schemeEnd);
    std::transform(scheme.begin(), scheme.end(), scheme.begin(),
                   [](unsigned char c) { return (char)tolower(c); });
    if (scheme == "https") p.secure = true;
    else if (scheme != "http") return p;

    std::string rest = url.substr(schemeEnd + 3);
    size_t pathStart = rest.find_first_of("/?");
    std::string hostPort = (pathStart == std::string::npos) ? rest : rest.substr(0, pathStart);
    if (pathStart == std::string::npos) p.pathAndQuery = "/";
    else if (rest[pathStart] == '?') p.pathAndQuery = "/" + rest.substr(pathStart);
    else p.pathAndQuery = rest.substr(pathStart);

    int defPort = p.secure ? 443 : 80;
    if (hostPort.empty()) return p;
    size_t colon = hostPort.rfind(':');
    if (colon != std::string::npos && colon + 1 < hostPort.length())
    {
        std::string portText = hostPort.substr(colon + 1);
        bool digits = !portText.empty();
        for (char c : portText)
            if (!isdigit((unsigned char)c)) { digits = false; break; }
        if (digits)
        {
            p.host = hostPort.substr(0, colon);
            p.port = atoi(portText.c_str());
        }
        else
        {
            p.host = hostPort;
            p.port = defPort;
        }
    }
    else
    {
        p.host = hostPort;
        p.port = defPort;
    }
    p.valid = !p.host.empty() && p.port > 0;
    return p;
}

static std::wstring ToWideAscii(const std::string& s)
{
    return std::wstring(s.begin(), s.end()); // host/path/headers 均 ASCII
}

std::string Translator::HttpOnce(const ParsedUrl& url, bool isGet,
                                 const std::string& body, const std::string& authHeaderLine)
{
    if (!m_hSession)
    {
        m_hSession = WinHttpOpen(L"WoWTranslateDirect/1.0",
                                 WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                 WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!m_hSession)
        {
            WT_LOG_ERROR("WinHttpOpen failed");
            return "";
        }
        WinHttpSetTimeouts((HINTERNET)m_hSession,
                           HTTP_TIMEOUTS[0], HTTP_TIMEOUTS[1], HTTP_TIMEOUTS[2], HTTP_TIMEOUTS[3]);
    }

    HINTERNET hConnect = WinHttpConnect((HINTERNET)m_hSession,
                                        ToWideAscii(url.host).c_str(),
                                        (INTERNET_PORT)url.port, 0);
    if (!hConnect)
    {
        DWORD e = GetLastError();
        WT_LOG_ERROR("WinHttpConnect failed host=" + url.host + " err=" + std::to_string(e));
        return "";
    }

    HINTERNET hRequest = WinHttpOpenRequest(hConnect,
                                            isGet ? L"GET" : L"POST",
                                            ToWideAscii(url.pathAndQuery).c_str(),
                                            NULL, WINHTTP_NO_REFERER,
                                            WINHTTP_DEFAULT_ACCEPT_TYPES,
                                            url.secure ? WINHTTP_FLAG_SECURE : 0);
    if (!hRequest)
    {
        WinHttpCloseHandle(hConnect);
        return "";
    }

    std::string headers = "Content-Type: application/json\r\nAccept: application/json\r\n";
    if (!authHeaderLine.empty())
        headers += authHeaderLine + "\r\n";

    std::string resp;
    BOOL sent = WinHttpSendRequest(hRequest,
                                   ToWideAscii(headers).c_str(),
                                   (DWORD)-1,
                                   (isGet || body.empty()) ? WINHTTP_NO_REQUEST_DATA : (LPVOID)body.data(),
                                   isGet ? 0 : (DWORD)body.size(),
                                   isGet ? 0 : (DWORD)body.size(),
                                   0);

    if (sent && WinHttpReceiveResponse(hRequest, NULL))
    {
        DWORD status = 0, sz = sizeof(status);
        WinHttpQueryHeaders(hRequest,
                            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX,
                            &status, &sz, WINHTTP_NO_HEADER_INDEX);
        {
            std::lock_guard<std::mutex> lk(m_cfgMx);
            m_lastHttpStatus = status;
        }

        DWORD avail = 0;
        char buf[8192];
        while (WinHttpQueryDataAvailable(hRequest, &avail) && avail > 0)
        {
            DWORD toRead = avail < sizeof(buf) ? avail : sizeof(buf);
            DWORD got = 0;
            if (!WinHttpReadData(hRequest, buf, toRead, &got) || got == 0)
                break;
            resp.append(buf, got);
        }
    }
    else
    {
        DWORD e = GetLastError();
        WT_LOG_ERROR("HTTP request failed err=" + std::to_string(e));
    }

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    return resp;
}

// ==================== 缓存 ====================

std::string Translator::CacheKey(const std::string& text, const std::string& from, const std::string& to)
{
    int pid;
    {
        std::lock_guard<std::mutex> lk(m_cfgMx);
        pid = (int)m_provider;
    }
    std::ostringstream os;
    os << pid << ":" << from << "->" << to << ":" << text.length() << ":" << text;
    return os.str();
}

std::string Translator::CacheGet(const std::string& key)
{
    std::lock_guard<std::mutex> lk(m_cacheMx);
    auto it = m_cache.find(key);
    if (it == m_cache.end()) return "";
    if (GetTickCount() - it->second.second > CACHE_TTL_MS)
    {
        m_cache.erase(it);
        return "";
    }
    return it->second.first;
}

void Translator::CachePut(const std::string& key, const std::string& value)
{
    std::lock_guard<std::mutex> lk(m_cacheMx);
    m_cache[key] = { value, GetTickCount() };
    if (m_cache.size() > CACHE_MAX)
        m_cache.erase(m_cache.begin()); // map 有序，最旧插入未追踪 → 删首项近似淘汰
}

std::string Translator::UrlEncode(const std::string& s)
{
    static const char* hex = "0123456789ABCDEF";
    std::string out;
    out.reserve(s.size() * 3);
    for (unsigned char c : s)
    {
        if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~')
            out += (char)c;
        else
        {
            out += '%';
            out += hex[(c >> 4) & 0xF];
            out += hex[c & 0xF];
        }
    }
    return out;
}

} // namespace wt
