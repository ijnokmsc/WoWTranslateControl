using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Providers;

/// <summary>
/// Provider 管理器（v50 添加模式）：ActiveProviderId 指向唯一生效服务。
///   "local" → 本地 llama；其余 → Providers 列表对应条目（openai 兼容 / 谷歌免费）。
/// 服务不存在（如条目被删）时回落本地 llama 并记日志。
/// 失败时返回 Ok=false，代理据此回显原文（不吞聊天）。
/// </summary>
public sealed class ProviderManager
{
    /// <summary>本地 llama 服务在 ActiveProviderId 里的保留 Id。</summary>
    public const string LocalId = "local";

    private readonly AppConfig _cfg;
    private readonly LlamaCppUpstreamProvider _llama;
    private readonly Dictionary<string, OpenAiCompatProvider> _openai = new();
    private readonly Dictionary<string, GoogleFreeProvider> _google = new();

    public event Action<string>? OnLog;

    public ProviderManager(AppConfig cfg)
    {
        _cfg = cfg;
        _llama = new LlamaCppUpstreamProvider(cfg);
    }

    public ProviderEntry? ActiveEntry =>
        _cfg.Providers.FirstOrDefault(p => p.Id == _cfg.ActiveProviderId);

    /// <summary>当前服务是否需要本地 llama-server 进程（配置失效也回落本地）。</summary>
    public bool NeedsLlama => _cfg.ActiveProviderId == LocalId || ActiveEntry == null;

    /// <summary>当前服务的显示名（UI / 日志用）。</summary>
    public string ActiveDisplayName
    {
        get
        {
            if (NeedsLlama) return "本地 llama-server（本地 AI）";
            var e = ActiveEntry!;
            return $"{e.Name}（{e.TypeLabel}）";
        }
    }

    /// <summary>当前服务的 Provider 类型 Id（诊断/测试输出用）。</summary>
    public string PrimaryId =>
        NeedsLlama ? LlamaCppUpstreamProvider.ProviderId
        : ActiveEntry!.Type == "google" ? GoogleFreeProvider.ProviderId
        : OpenAiCompatProvider.ProviderId;

    public async Task<ProviderReply> TranslateAsync(TranslationContext ctx, CancellationToken ct)
    {
        var entry = ActiveEntry;
        if (NeedsLlama)
        {
            if (_cfg.ActiveProviderId != LocalId)
                OnLog?.Invoke($"配置的翻译服务 {_cfg.ActiveProviderId} 不存在（可能已被删除），回落本地 llama");
            return await _llama.TranslateAsync(ctx, ct).ConfigureAwait(false);
        }

        ITranslationProvider provider;
        if (entry.Type == "google")
        {
            if (!_google.TryGetValue(entry.Id, out var g))
                _google[entry.Id] = g = new GoogleFreeProvider(_cfg, entry);
            provider = g;
        }
        else
        {
            if (!_openai.TryGetValue(entry.Id, out var o))
                _openai[entry.Id] = o = new OpenAiCompatProvider(_cfg, entry);
            provider = o;
        }

        var reply = await provider.TranslateAsync(ctx, ct).ConfigureAwait(false);
        if (!reply.Ok)
            OnLog?.Invoke($"Provider {entry.Name} 失败：{reply.Error}");
        return reply;
    }
}
