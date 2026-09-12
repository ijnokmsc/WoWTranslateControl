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

/// <summary>
/// OpenAI 兼容端点 Provider（第三方 API：OpenAI / DeepSeek / 硅基流动 / 本机其他推理服务等）。
/// 请求体由本层从零构造（system 指令 + user 文本），不依赖插件原始请求的形状。
/// </summary>
public sealed class OpenAiCompatProvider : ITranslationProvider
{
    public const string ProviderId = "openai-compat";

    private readonly AppConfig _cfg;
    public OpenAiCompatProvider(AppConfig cfg) => _cfg = cfg;

    public string Id => ProviderId;

    public async Task<ProviderReply> TranslateAsync(TranslationContext ctx, CancellationToken ct)
    {
        try
        {
            var body = BuildRequest(ctx);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new("application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.OpenAiEndpoint)
            {
                Content = content
            };
            if (!string.IsNullOrEmpty(_cfg.OpenAiApiKey))
                req.Headers.Authorization = new("Bearer", _cfg.OpenAiApiKey);

            using var resp = await ProxyServer.SharedHttp
                .SendAsync(req, _cfg.UpstreamTimeoutSec, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var translated = ExtractContent(text);
            if (translated == null)
                return new ProviderReply(false, text, ctx.OriginalText,
                    $"响应不含 choices[0].message.content：{Trunc(text)}");
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

    internal byte[] BuildRequest(TranslationContext ctx)
    {
        var systemPrompt = ctx.TargetLang == "en"
            ? AppConfig.DefaultSystemPromptZh2En
            : _cfg.SystemPrompt;
        if (ctx.GlossaryApplied)
            systemPrompt += Glossary.GlossaryApplier.PromptAddendum;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", string.IsNullOrWhiteSpace(_cfg.OpenAiModel)
                ? "gpt-4.1-mini" : _cfg.OpenAiModel);
            w.WritePropertyName("messages");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", systemPrompt);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", ctx.Text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteNumber("max_tokens", _cfg.MaxTokensCeil);
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var ch) &&
                ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0 &&
                ch[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
                return content.GetString();
        }
        catch { }
        return null;
    }

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
