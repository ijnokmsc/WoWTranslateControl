using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Core.Providers;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core;

/// <summary>一条流量的分类结果，供界面展示与统计。</summary>
public enum TrafficKind
{
    Filtered,      // 命中过滤规则，未走模型
    CacheHit,      // 缓存命中，未走模型
    Model,         // 真正转发给模型
    UpstreamError, // 上游错误
    BadRequest,    // 请求解析失败
}

public sealed record TrafficEntry(
    DateTime Time,
    TrafficKind Kind,
    string RuleId,
    string RuleName,
    string Content,
    int ElapsedMs,
    string? Channel = null);

/// <summary>
/// 本地翻译代理（v2：管线驱动）。
///
/// 为什么手写 HTTP 而不用 HttpListener：
///   实测 HttpListener 绑定 127.0.0.1:8080 抛 HttpListenerException("句柄无效")，
///   因为它依赖 http.sys 内核驱动，需要管理员权限或 urlacl 预留。
///   而 TcpListener 在普通权限下可直接绑定（已实测通过），
///   这样程序可以双击运行，无需以管理员身份启动。
///
/// 链路：WoWTranslate 插件 → 本程序 8080 → llama-server 8081
/// v2 管线：Filter(R0-R6) → Glossary(术语占位符) → Cache → Provider(llama/云端)
/// </summary>
public sealed class ProxyServer : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly GlossaryStore _glossary;
    private readonly CacheStage _cacheStage;
    private readonly ProviderManager _providers;
    private readonly IMessageStage[] _stages;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public bool IsRunning => _running;

    public event Action<TrafficEntry>? OnTraffic;
    public event Action<string>? OnLog;

    public long TotalRequests;
    public long FilteredCount;
    public long CacheHitCount;
    public long ModelCount;
    public long ErrorCount;

    public ProxyServer(AppConfig cfg, GlossaryStore? glossary = null)
    {
        _cfg = cfg;
        _glossary = glossary ?? GlossaryStore.Load(
            Path.Combine(AppContext.BaseDirectory, "glossary.json"));
        _cacheStage = new CacheStage(cfg, _glossary,
            Path.Combine(AppContext.BaseDirectory, "cache.json"),
            new ProviderManager(cfg).PrimaryId);
        _providers = new ProviderManager(cfg);
        _providers.OnLog += m => OnLog?.Invoke(m);
        _stages = new IMessageStage[]
        {
            new FilterStage(cfg),
            new GlossaryStage(cfg, _glossary),
            _cacheStage,
        };
    }

    /// <summary>术语表（供 UI 管理）。增删改自动 bump 版本并失效相关缓存。</summary>
    public GlossaryStore Glossary => _glossary;

    public void Start()
    {
        if (_running) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _cfg.ListenPort);
        _listener.Start();
        _running = true;

        OnLog?.Invoke($"代理已启动：监听 127.0.0.1:{_cfg.ListenPort} → 转发 127.0.0.1:{_cfg.UpstreamPort}" +
                      (_glossary.Count > 0 ? $"（术语表 {_glossary.Count} 条）" : ""));
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cacheStage.SaveNow();
        OnLog?.Invoke("代理已停止");
    }

    public void ClearCache()
    {
        _cacheStage.Clear();
        OnLog?.Invoke("响应缓存已清空");
    }

    public int CacheCount => _cacheStage.Count;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; continue; }

            // 每个连接独立处理，互不阻塞
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                // 插件侧可能复用连接，循环处理直到对端关闭
                while (_running && !ct.IsCancellationRequested)
                {
                    if (!await HandleOneRequestAsync(stream, ct).ConfigureAwait(false)) break;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 对端断开，正常情况
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"连接处理异常：{ex.Message}");
            }
        }
    }

    /// <summary>处理单个 HTTP 请求。返回 false 表示应关闭连接。</summary>
    private async Task<bool> HandleOneRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headBuf = new MemoryStream();
        var one = new byte[1];
        // 逐字节读到头结束，避免读多吞掉 body 起始字节
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return false; // 对端关闭
            headBuf.WriteByte(one[0]);
            var arr = headBuf.ToArray();
            if (arr.Length >= 4 &&
                arr[^4] == (byte)'\r' && arr[^3] == (byte)'\n' &&
                arr[^2] == (byte)'\r' && arr[^1] == (byte)'\n') break;
            if (arr.Length > 65536) return false; // 头部异常，断开
        }

        var headText = Encoding.UTF8.GetString(headBuf.ToArray());
        var lines = headText.Split("\r\n", StringSplitOptions.None);
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return false;
        var method = reqLine[0].ToUpperInvariant();
        var path = reqLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(':');
            if (idx > 0) headers[lines[i][..idx].Trim()] = lines[i][(idx + 1)..].Trim();
        }

        var keepAlive = !string.Equals(headers.GetValueOrDefault("Connection"), "close",
            StringComparison.OrdinalIgnoreCase);

        // 读取 body
        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var clStr) &&
            int.TryParse(clStr, out var cl) && cl > 0 && cl < 32 * 1024 * 1024)
        {
            body = new byte[cl];
            var off = 0;
            while (off < cl)
            {
                var n = await stream.ReadAsync(body.AsMemory(off, cl - off), ct).ConfigureAwait(false);
                if (n == 0) break;
                off += n;
            }
        }
        else if (string.Equals(headers.GetValueOrDefault("Transfer-Encoding"), "chunked",
                     StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(stream, ct).ConfigureAwait(false);
        }

        await RouteAsync(stream, method, path, body, keepAlive, ct).ConfigureAwait(false);
        return keepAlive;
    }

    private static async Task<byte[]> ReadChunkedAsync(NetworkStream stream, CancellationToken ct)
    {
        var result = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            if (line == null) break;
            if (!int.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)) break;
            if (size == 0) { await ReadLineAsync(stream, ct).ConfigureAwait(false); break; }
            var buf = new byte[size];
            var off = 0;
            while (off < size)
            {
                var n = await stream.ReadAsync(buf.AsMemory(off, size - off), ct).ConfigureAwait(false);
                if (n == 0) break;
                off += n;
            }
            result.Write(buf, 0, off);
            await ReadLineAsync(stream, ct).ConfigureAwait(false);
        }
        return result.ToArray();
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (one[0] == (byte)'\n')
            {
                var s = Encoding.UTF8.GetString(ms.ToArray());
                return s.EndsWith('\r') ? s[..^1] : s;
            }
            ms.WriteByte(one[0]);
            if (ms.Length > 65536) return null;
        }
    }

    private async Task RouteAsync(NetworkStream stream, string method, string path,
        byte[] body, bool keepAlive, CancellationToken ct)
    {
        Interlocked.Increment(ref TotalRequests);
        var sw = Stopwatch.StartNew();

        // 健康检查端点，供界面和外部探活
        if (path.StartsWith("/__health", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(stream, 200, "{\"status\":\"ok\"}", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        // 只有 chat/completions 需要解析与管线处理，其余原样透传
        if (method == "POST" && path.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            await HandleChatAsync(stream, body, keepAlive, sw, ct).ConfigureAwait(false);
            return;
        }

        await ForwardRawAsync(stream, method, path, body, keepAlive, sw, ct).ConfigureAwait(false);
    }

    // ==================== v2 管线 ====================

    private async Task HandleChatAsync(NetworkStream stream, byte[] body, bool keepAlive,
        Stopwatch sw, CancellationToken ct)
    {
        string userContent;
        try
        {
            using var probe = JsonDocument.Parse(body);
            userContent = ExtractUserContent(probe);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ErrorCount);
            Emit(TrafficKind.BadRequest, "ERR", "请求解析失败", ex.Message, sw);
            await SendAsync(stream, 400, "{\"error\":\"bad json\"}", keepAlive, ct).ConfigureAwait(false);
            return;
        }

        // 频道标签剥离（无条件）：\1CH\1 前缀由插件 Lua 补丁注入，绝不进模型/缓存
        var channel = MessageFilter.ParseChannelTag(userContent, out var cleanText);
        var ctx = new TranslationContext(cleanText, body, _cfg) { Channel = channel };

        // 落盘留证：DLL 实际发来的请求文本 + 原始报文前 120 字节 hex（诊断编码损坏用）
        if (_cfg.WriteFileLog) WriteTrafficLog(channel, cleanText, body);

        // ---- Filter → Glossary → Cache ----
        foreach (var stage in _stages)
        {
            PipelineResult? result;
            try
            {
                result = await stage.ProcessAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个阶段异常不应中断翻译链路：记错误并放行
                OnLog?.Invoke($"阶段 {stage.Id} 异常：{ex.Message}");
                result = null;
            }

            if (result == null) continue;
            if (result.Kind == TrafficKind.CacheHit) Interlocked.Increment(ref CacheHitCount);
            else Interlocked.Increment(ref FilteredCount);

            Emit(result.Kind, result.RuleId, result.RuleName, result.Content, sw);
            var resp = BuildOpenAiResponse(result.Content);
            await SendAsync(stream, 200, resp, keepAlive, ct).ConfigureAwait(false);
            return;
        }

        // ---- Provider（llama / OpenAI 兼容 / 谷歌免费，按模式与故障转移链）----
        var reply = await _providers.TranslateAsync(ctx, ct).ConfigureAwait(false);
        if (_cfg.WriteFileLog)
            WriteTrafficLog(channel, "[REPLY:" + (reply.Ok ? "OK" : "FAIL") + "] " + reply.Translated, null);

        string finalText;
        TrafficKind kind;
        string ruleId;
        string ruleName;
        if (reply.Ok)
        {
            // 术语占位符还原（缓存里存还原后的最终译文）
            var (restored, _) = GlossaryApplier.Restore(reply.Translated, ctx.GlossaryMap);
            finalText = string.IsNullOrEmpty(restored) ? ctx.OriginalText : restored;
            kind = TrafficKind.Model;
            ruleId = "-";
            ruleName = "模型翻译";
            _cacheStage.Put(ctx, finalText);
            Interlocked.Increment(ref ModelCount);
        }
        else
        {
            // 上游不可用时回显原文，保证游戏内不报错、聊天不被吞（v1 已验证取舍）
            finalText = ctx.OriginalText;
            kind = TrafficKind.UpstreamError;
            ruleId = "ERR";
            ruleName = "上游失败";
            Interlocked.Increment(ref ErrorCount);
        }

        Emit(kind, ruleId, ruleName, kind == TrafficKind.UpstreamError
            ? $"{ctx.OriginalText} | {reply.Error}" : ctx.OriginalText, sw, channel);

        // 2026-09-11 游戏实测：模型路径不再透传 llama 的原生 UTF-8 响应体，统一走
        // BuildOpenAiResponse（默认转义器把非 ASCII 写成 \uXXXX 纯 ASCII）。
        // 背景：WoWTranslate335.dll 在入站路径把原生 UTF-8 中文逐字转成 '?'（宽字符→ANSI
        // 转换特征），英文请求→中文译文同样损坏（"BOSS" 存活、中文全灭）。若 DLL 的 JSON
        // 解析器认识 \u 转义（走十六进制→宽字符路径，不经 ANSI 转换），此改动即绕过损坏；
        // 2026-09-11 晚实测结论：\uXXXX 转义试验失败——DLL 解码层不认 \uXXXX，
        // 游戏内中文全部变 ?（与原生 UTF-8 同样损坏）。两种编码都过不了 DLL 入站层，
        // 说明损坏在 DLL 宽字符→ANSI 输出层，服务器侧无解，只能 Track B 自有 DLL 根治。
        // 恢复原生 UTF-8 透传：行为与 v1 python 代理一致，且插件 Lua 词库仍可在
        // 残存 ASCII（如 BOSS）上做术语替换。模型路径透传 llama 原始响应体。
        var payload = kind == TrafficKind.Model
            ? Encoding.UTF8.GetBytes(reply.RawBody)
            : Encoding.UTF8.GetBytes(BuildOpenAiResponse(finalText));
        await SendRawAsync(stream, 200, payload, "application/json", keepAlive, ct)
            .ConfigureAwait(false);
    }

    /// <summary>从请求 JSON 中取出最后一条 user 消息作为待译文本。</summary>
    private static readonly object _trafficLogLock = new();

    /// <summary>
    /// 将 DLL 发来的请求原文与原始报文 hex 追加到 proxy_traffic.log。
    /// 诊断插件编码损坏的唯一铁证：能看出 DLL 到底发的是 UTF-8 中文、\uXXXX 还是 '?????'。
    /// </summary>
    private static void WriteTrafficLog(string? channel, string text, byte[]? rawBody)
    {
        try
        {
            var hex = rawBody == null
                ? "-"
                : string.Concat(rawBody.Take(120).Select(b => b.ToString("x2")));
            var line = $"{DateTime.Now:HH:mm:ss.fff}\t{(channel ?? "-")}\t{text.Replace('\n', ' ')}\tHEX:{hex}\n";
            lock (_trafficLogLock)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "proxy_traffic.log"), line, new System.Text.UTF8Encoding(false));
        }
        catch { /* 日志失败不影响翻译 */ }
    }

    private static string ExtractUserContent(JsonDocument doc)
    {
        var userContent = string.Empty;
        if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
            msgs.ValueKind == JsonValueKind.Array)
        {
            for (var i = msgs.GetArrayLength() - 1; i >= 0; i--)
            {
                var m = msgs[i];
                if (m.TryGetProperty("role", out var r) && r.GetString() == "user" &&
                    m.TryGetProperty("content", out var c))
                {
                    userContent = c.GetString() ?? string.Empty;
                    break;
                }
            }
        }
        return userContent;
    }

    private async Task ForwardRawAsync(NetworkStream stream, string method, string path,
        byte[] body, bool keepAlive, Stopwatch sw, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method),
                $"http://127.0.0.1:{_cfg.UpstreamPort}{path}");
            if (body.Length > 0 && method is "POST" or "PUT" or "PATCH")
                req.Content = new ByteArrayContent(body);
            using var resp = await SharedHttp.SendAsync(req, _cfg.UpstreamTimeoutSec, ct)
                .ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Emit(TrafficKind.Model, "-", "透传", $"{method} {path}", sw);
            await SendRawAsync(stream, (int)resp.StatusCode, bytes,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json", keepAlive, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ErrorCount);
            Emit(TrafficKind.UpstreamError, "ERR", "透传失败", ex.Message, sw);
            await SendAsync(stream, 502, $"{{\"error\":\"upstream unreachable\"}}", keepAlive, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 进程级共享 HttpClient（SocketsHttpHandler 连接池）。
    /// 修复 v1 每请求 new HttpClient 的 TIME_WAIT 耗尽问题。
    /// </summary>
    internal static class SharedHttp
    {
        internal static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 16,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // 每请求用 CTS 控制
        };

        public static async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, int timeoutSec, CancellationToken ct)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            return await Client.SendAsync(req, linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>构造一个最小的 OpenAI 兼容响应，content 即译文。</summary>
    private static string BuildOpenAiResponse(string translated)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("id", "chatcmpl-wtc");
            w.WriteString("object", "chat.completion");
            w.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.WriteString("model", "WoWTranslateControl");
            w.WritePropertyName("choices");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteNumber("index", 0);
            w.WritePropertyName("message");
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            w.WriteString("content", translated);
            w.WriteEndObject();
            w.WriteString("finish_reason", "stop");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task SendAsync(NetworkStream stream, int status, string body,
        bool keepAlive, CancellationToken ct)
        => await SendRawAsync(stream, status, Encoding.UTF8.GetBytes(body),
            "application/json", keepAlive, ct).ConfigureAwait(false);

    private static async Task SendRawAsync(NetworkStream stream, int status, byte[] body,
        string contentType, bool keepAlive, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            429 => "Too Many Requests",
            502 => "Bad Gateway",
            _ => "OK"
        };
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Emit(TrafficKind kind, string ruleId, string ruleName, string content,
        Stopwatch sw, string? channel = null)
    {
        sw.Stop();
        var entry = new TrafficEntry(DateTime.Now, kind, ruleId, ruleName,
            Truncate(content, 160), (int)sw.ElapsedMilliseconds, channel);
        OnTraffic?.Invoke(entry);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        Stop();
        _cacheStage.Dispose();
        _cts?.Dispose();
    }
}
