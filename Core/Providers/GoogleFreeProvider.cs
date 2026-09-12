using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Providers;

/// <summary>
/// 谷歌免费翻译 Provider（translate.googleapis.com client=gtx 公开端点，无需密钥）。
/// 与插件内置 google_free 走同一公开端点；作为兜底/轻量档位使用。
/// 响应形如 [[["译","orig",null,null,10],["文","text",...]],null,"en",...]。
/// </summary>
public sealed class GoogleFreeProvider : ITranslationProvider
{
    public const string ProviderId = "google-free";

    private readonly AppConfig _cfg;
    public GoogleFreeProvider(AppConfig cfg) => _cfg = cfg;

    public string Id => ProviderId;

    public async Task<ProviderReply> TranslateAsync(TranslationContext ctx, CancellationToken ct)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      "?client=gtx&dt=t" +
                      $"&sl={Uri.EscapeDataString(ctx.SourceLang ?? _cfg.GoogleSl)}" +
                      $"&tl={Uri.EscapeDataString(ctx.TargetLang ?? _cfg.GoogleTl)}" +
                      $"&q={Uri.EscapeDataString(ctx.Text)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("WoWTranslateControl/2.0");

            using var resp = await ProxyServer.SharedHttp
                .SendAsync(req, Math.Max(10, _cfg.UpstreamTimeoutSec), ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var translated = ParseGoogleResponse(text);
            if (string.IsNullOrEmpty(translated))
                return new ProviderReply(false, text, ctx.OriginalText,
                    $"谷歌响应解析失败：{Trunc(text)}");
            return new ProviderReply(true, text, translated!, "");
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException && !ct.IsCancellationRequested
                ? "谷歌上游超时"
                : ex.Message;
            return new ProviderReply(false, "", ctx.OriginalText, msg);
        }
    }

    /// <summary>解析 translate_a/single 响应，拼接所有分段的译文。纯函数，可单测。</summary>
    public static string? ParseGoogleResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
                return null;
            if (root[0].ValueKind != JsonValueKind.Array)
                return null;

            var sb = new StringBuilder();
            foreach (var seg in root[0].EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                    seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            }
            var s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
