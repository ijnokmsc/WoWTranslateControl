using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;

namespace WoWTranslateControl.Core.Providers;

/// <summary>
/// 翻译 Provider 抽象。所有实现面向同一管线：
/// 输入 TranslationContext（Text 已过过滤/术语阶段），输出 ProviderReply。
/// 失败一律返回 Ok=false + Error，由上层决定回退或回显原文，绝不抛出。
/// </summary>
public interface ITranslationProvider
{
    /// <summary>稳定标识（参与缓存 key，改动会使旧缓存失效）。</summary>
    string Id { get; }

    Task<ProviderReply> TranslateAsync(TranslationContext ctx, System.Threading.CancellationToken ct);
}
