using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Pipeline;

/// <summary>
/// 频道过滤（C0-C8）+ 内部质检规则（R0/R7/R8）阶段。
/// 频道规则先行：明确关闭的频道直接回显原文，连内容判定都不必做。
/// 规则本体在 MessageFilter，此处只做管线包装。
/// </summary>
public sealed class FilterStage : IMessageStage
{
    private readonly AppConfig _cfg;
    public FilterStage(AppConfig cfg) => _cfg = cfg;

    public string Id => "filter";

    public Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct)
    {
        // 外发翻译：正文是玩家刚敲的中文，R8"已含中文"规则必然误杀 → 跳过内容规则
        if (ctx.Outgoing) return Task.FromResult<PipelineResult?>(null);

        var channelDecision = MessageFilter.CheckChannel(ctx.Channel, _cfg);
        if (channelDecision != null)
        {
            return Task.FromResult<PipelineResult?>(new PipelineResult(
                TrafficKind.Filtered, channelDecision.RuleId, channelDecision.RuleName,
                ctx.OriginalText));
        }

        var d = MessageFilter.Classify(ctx.OriginalText, _cfg);
        if (d.Filtered)
        {
            return Task.FromResult<PipelineResult?>(new PipelineResult(
                TrafficKind.Filtered, d.RuleId, d.RuleName, ctx.OriginalText));
        }
        return Task.FromResult<PipelineResult?>(null);
    }
}
