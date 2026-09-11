using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Pipeline;

/// <summary>
/// 一条待译请求在管线中的上下文。
/// 各 Stage 依序处理：改写 Text、挂载术语映射表，或短路返回 PipelineResult。
/// </summary>
public sealed class TranslationContext
{
    public TranslationContext(string originalText, byte[] rawBody, AppConfig cfg)
    {
        OriginalText = originalText;
        Text = originalText;
        RawBody = rawBody;
        Config = cfg;
    }

    /// <summary>进入管线时的原始待译文本（用户消息），不可变，用于兜底回显。频道标签已剥离。</summary>
    public string OriginalText { get; }

    /// <summary>
    /// 频道标识（SAY/YELL/WHISPER/PARTY/GUILD/RAID/BATTLEGROUND/CHANNEL）。
    /// 由插件 Lua 补丁以 \1CH\1 前缀搭车上报，代理在进管线前剥离；null = 未标记（旧 Lua / 外发请求）。
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>当前文本，管线各阶段可改写（术语替换等）。</summary>
    public string Text { get; set; }

    /// <summary>插件的原始请求体（未解析），供 Provider 层改写转发。</summary>
    public byte[] RawBody { get; }

    public AppConfig Config { get; }

    /// <summary>术语占位符 → 目标语术语 的映射（GlossaryStage 填充）。</summary>
    public IReadOnlyDictionary<string, string> GlossaryMap { get; set; }
        = new Dictionary<string, string>();

    /// <summary>本条请求是否发生了术语替换。</summary>
    public bool GlossaryApplied { get; set; }

    /// <summary>CacheStage 计算出的缓存 key，供翻译完成后回写。</summary>
    public string? CacheKey { get; set; }
}

/// <summary>Stage 短路结果：直接以此内容回包给插件，不再进入后续阶段。</summary>
public sealed record PipelineResult(
    TrafficKind Kind,
    string RuleId,
    string RuleName,
    string Content);

/// <summary>管线阶段。返回 null = 放行进入下一阶段；返回结果 = 短路。</summary>
public interface IMessageStage
{
    string Id { get; }
    Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct);
}
