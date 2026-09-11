using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Pipeline;

/// <summary>
/// 频道过滤（C0-C8）+ 内容过滤（R0-R6）阶段。
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
