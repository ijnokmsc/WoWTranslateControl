using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Providers;

/// <summary>
/// Provider 管理器：按模式组装链路，逐个尝试，首个成功者胜出。
///   local  = [llama]
///   openai = [openai]
///   google = [google]
///   auto   = [llama, openai, google]   ← 故障转移：本地挂了自动落到云端
/// 全部失败时返回最后一个 Ok=false，代理据此回显原文（不吞聊天）。
/// </summary>
public sealed class ProviderManager
{
    private readonly AppConfig _cfg;
    private readonly LlamaCppUpstreamProvider _llama;
    private readonly OpenAiCompatProvider _openai;
    private readonly GoogleFreeProvider _google;

    public event Action<string>? OnLog;

    public ProviderManager(AppConfig cfg)
    {
        _cfg = cfg;
        _llama = new LlamaCppUpstreamProvider(cfg);
        _openai = new OpenAiCompatProvider(cfg);
        _google = new GoogleFreeProvider(cfg);
    }

    public static IReadOnlyList<string> BuildChain(string mode) => mode switch
    {
        "local" => new[] { LlamaCppUpstreamProvider.ProviderId },
        "openai" => new[] { OpenAiCompatProvider.ProviderId },
        "google" => new[] { GoogleFreeProvider.ProviderId },
        "auto" => new[]
        {
            LlamaCppUpstreamProvider.ProviderId,
            OpenAiCompatProvider.ProviderId,
            GoogleFreeProvider.ProviderId,
        },
        _ => new[] { LlamaCppUpstreamProvider.ProviderId },
    };

    /// <summary>链路首个 Provider 的 Id（参与缓存 key；改模式即换缓存空间）。</summary>
    public string PrimaryId => BuildChain(_cfg.ProviderMode)[0];

    public async Task<ProviderReply> TranslateAsync(TranslationContext ctx, CancellationToken ct)
    {
        ProviderReply last = new(false, "", ctx.OriginalText, "无可用 Provider");
        foreach (var id in BuildChain(_cfg.ProviderMode))
        {
            var provider = id switch
            {
                LlamaCppUpstreamProvider.ProviderId => _llama,
                OpenAiCompatProvider.ProviderId => _openai,
                GoogleFreeProvider.ProviderId => _google,
                _ => (ITranslationProvider?)null,
            };
            if (provider == null) continue;

            var reply = await provider.TranslateAsync(ctx, ct).ConfigureAwait(false);
            if (reply.Ok) return reply;

            last = reply;
            OnLog?.Invoke($"Provider {id} 失败：{reply.Error}，尝试链路下一节点");
        }
        return last;
    }
}
