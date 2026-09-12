using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Providers;

public sealed record ProviderReply(bool Ok, string RawBody, string Translated, string Error);

/// <summary>
/// 本地 llama-server（OpenAI 兼容）Provider。
///
/// 职责沿用 v1 已验证逻辑：
///   - 改写请求：注入 system 指令（插件 DLL 发来的是空串）、钳制 max_tokens、
///     强制 stream=false、丢弃插件发来的空 system；
///   - 术语替换发生时，追加 ⟦Gn⟧ 占位符保留说明；
///   - 超时/失败向上返回 Ok=false，由代理决定回显原文（不吞聊天）。
///
/// S3 将在此接口下扩展 OpenAiCompat / GoogleCloud / CustomHttp 等实现。
/// </summary>
public sealed class LlamaCppUpstreamProvider : ITranslationProvider
{
    public const string ProviderId = "llama-local";

    private readonly AppConfig _cfg;

    public LlamaCppUpstreamProvider(AppConfig cfg) => _cfg = cfg;

    public string Id => ProviderId;

    public async Task<ProviderReply> TranslateAsync(TranslationContext ctx, CancellationToken ct)
    {
        var rewritten = RewriteRequest(ctx);
        try
        {
            using var content = new ByteArrayContent(rewritten);
            content.Headers.ContentType = new("application/json");
            var url = $"http://127.0.0.1:{_cfg.UpstreamPort}/v1/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var resp = await ProxyServer.SharedHttp.SendAsync(req, _cfg.UpstreamTimeoutSec, ct)
                .ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var translated = ExtractContent(text) ?? ctx.OriginalText;
            return new ProviderReply(true, text, translated, "");
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException && !ct.IsCancellationRequested
                ? $"上游超时（{_cfg.UpstreamTimeoutSec}s）"
                : ex.Message;
            return new ProviderReply(false, "", ctx.OriginalText, msg);
        }
    }

    /// <summary>
    /// 改写发往模型的请求。改写失败时原样转发——
    /// 宁可翻译质量下降也不要中断服务（v1 已验证的取舍）。
    /// </summary>
    internal byte[] RewriteRequest(TranslationContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(ctx.RawBody);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                var wroteModel = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == "messages")
                    {
                        w.WritePropertyName("messages");
                        w.WriteStartArray();

                        // 强制第一条为 system 指令
                        var systemPrompt = _cfg.SystemPrompt;
                        if (ctx.GlossaryApplied)
                            systemPrompt += Core.Glossary.GlossaryApplier.PromptAddendum;
                        w.WriteStartObject();
                        w.WriteString("role", "system");
                        w.WriteString("content", systemPrompt);
                        w.WriteEndObject();

                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var m in prop.Value.EnumerateArray())
                            {
                                var role = m.TryGetProperty("role", out var rr) ? rr.GetString() : null;
                                if (role == "system") continue; // 丢弃插件发来的空 system
                                if (role == "user")
                                {
                                    // ⚠ 用管线剥离/术语处理后的 ctx.Text，绝不透传原始 body——
                                    // 原始 user content 带 \1<频道>\1 标签，模型会把 CHANNEL
                                    // 翻进译文（v22 实测：回包出现 "频道LFM 暗黑密码"）。
                                    w.WriteStartObject();
                                    w.WriteString("role", "user");
                                    w.WriteString("content", ctx.Text);
                                    w.WriteEndObject();
                                    continue;
                                }
                                m.WriteTo(w);
                            }
                        }
                        w.WriteEndArray();
                    }
                    else if (prop.Name is "max_tokens" or "max_completion_tokens")
                    {
                        // 统一在末尾写入钳制值
                    }
                    else
                    {
                        if (prop.Name == "model") wroteModel = true;
                        prop.WriteTo(w);
                    }
                }
                if (!wroteModel) w.WriteString("model", "HY-MT2-1.8B");
                w.WriteNumber("max_tokens", _cfg.MaxTokensCeil);
                w.WriteBoolean("stream", false);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }
        catch
        {
            return ctx.RawBody;
        }
    }

    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var ch) &&
                ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
            {
                var c0 = ch[0];
                if (c0.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                    return content.GetString();
            }
        }
        catch { }
        return null;
    }

    public void Dispose() { /* 单例 HttpClient 与进程同生命周期，不释放 */ }
}
