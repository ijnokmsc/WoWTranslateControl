using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Pipeline;

/// <summary>
/// 术语表阶段：把文本中的术语替换为 ⟦Gn⟧ 占位符。
/// 不短路；替换映射挂在 ctx 上，供 Provider 层附加提示词说明、
/// 翻译完成后由代理统一 Restore。
/// </summary>
public sealed class GlossaryStage : IMessageStage
{
    private readonly AppConfig _cfg;
    private readonly GlossaryStore _store;

    public GlossaryStage(AppConfig cfg, GlossaryStore store)
    {
        _cfg = cfg;
        _store = store;
    }

    public string Id => "glossary";

    public Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct)
    {
        if (!_cfg.GlossaryEnabled || _store.Count == 0)
            return Task.FromResult<PipelineResult?>(null);

        var (newText, map) = GlossaryApplier.Apply(ctx.Text, _store.Snapshot());
        if (map.Count > 0)
        {
            ctx.Text = newText;
            ctx.GlossaryMap = map;
            ctx.GlossaryApplied = true;
        }
        return Task.FromResult<PipelineResult?>(null);
    }
}
