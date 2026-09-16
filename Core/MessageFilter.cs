using System;
using System.Text.RegularExpressions;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core;

/// <summary>过滤判定结果。</summary>
public sealed record FilterDecision(bool Filtered, string RuleId, string RuleName, string Detail);

/// <summary>
/// 消息过滤引擎（3.0：R1-R6 用户内容规则已随 GS 插件链路移除——
/// Direct 引擎只捕获玩家聊天事件，战斗/拾取等系统播报根本不会进请求）。
/// 保留 R0/R7/R8 内部质检规则与 C0-C8 频道过滤。
/// </summary>
public static class MessageFilter
{
    private static readonly Regex ReMarkupH = new(@"\|H[^|]*\|h", RegexOptions.Compiled);
    // ---- R7 乱码兜底：GS DLL 出入站把中文逐字转成 '?'（宽字符→ANSI），连续 5+ 个 ? 即损坏文本 ----
    private static readonly Regex ReMangled = new(@"\?{4,}", RegexOptions.Compiled);
    private static readonly Regex ReMarkupT = new(@"\|T[^|]*\|t", RegexOptions.Compiled);
    private static readonly Regex ReMarkupColor = new(@"\|c[0-9a-fA-F]{8}|\|r|\|h", RegexOptions.Compiled);
    private static readonly Regex ReUrl = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ReCjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    // ---- 频道标签：插件 Lua 补丁在文本前加 \1<频道>\1，控制台无条件剥离并据此过滤 ----
    // [A-Z0-9_]：v24 外发翻译用 ZH2EN 作频道名，含数字
    private static readonly Regex ReChannelTag =
        new(@"^\x01([A-Z0-9_]+)\x01", RegexOptions.Compiled);

    /// <summary>
    /// 解析并剥离频道标签前缀（\1CH\1）。无论频道过滤开关与否都必须剥离——
    /// 标签是控制台内部协议，绝不能进模型、进缓存、进游戏。
    /// </summary>
    /// <returns>频道名（如 GUILD）；无标签返回 null。</returns>
    public static string? ParseChannelTag(string content, out string stripped)
    {
        content ??= string.Empty;
        var m = ReChannelTag.Match(content);
        if (m.Success)
        {
            stripped = content[m.Length..];
            return m.Groups[1].Value;
        }
        stripped = content ?? string.Empty;
        return null;
    }

    /// <summary>
    /// 频道过滤判定（C0-C8）。命中关闭的频道返回"已过滤"决策，放行返回 null。
    /// 在内部质检规则（R0/R7/R8）之前执行：频道不要的消息连内容判定都不必做。
    /// </summary>
    public static FilterDecision? CheckChannel(string? channel, AppConfig cfg)
    {
        if (!cfg.ChannelFilterEnabled) return null;

        if (channel == null)
        {
            return cfg.ChannelUntagged ? null : new FilterDecision(
                true, "C0", "未标记频道",
                "请求无频道标签（旧版 Lua 或外发翻译），按未标记频道规则跳过");
        }

        foreach (var (ruleId, wowName, cnName, cfgKey) in AppConfig.ChannelCatalog)
        {
            if (ruleId == "C0" || wowName != channel) continue;
            var enabled = cfgKey switch
            {
                "ChannelSay" => cfg.ChannelSay,
                "ChannelYell" => cfg.ChannelYell,
                "ChannelWhisper" => cfg.ChannelWhisper,
                "ChannelParty" => cfg.ChannelParty,
                "ChannelGuild" => cfg.ChannelGuild,
                "ChannelRaid" => cfg.ChannelRaid,
                "ChannelBattleground" => cfg.ChannelBattleground,
                "ChannelWorld" => cfg.ChannelWorld,
                _ => true,
            };
            return enabled ? null : new FilterDecision(
                true, ruleId, $"{cnName}频道关闭",
                $"{wowName} 频道已禁用翻译，直接回显原文");
        }

        // 未知频道名（插件新增事件映射）默认放行，交内容规则处理
        return null;
    }

    /// <summary>
    /// 剥离所有魔兽标记，只留下可能需要翻译的自然语言文本。
    /// 反复迭代以处理嵌套链接（插件正是因为处理不了嵌套才泄漏标记的）。
    /// </summary>
    public static string StripWoWMarkup(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = text;
        for (var i = 0; i < 8; i++)
        {
            var before = s;
            s = ReMarkupT.Replace(s, string.Empty);
            s = ReMarkupH.Replace(s, string.Empty);
            s = ReMarkupColor.Replace(s, string.Empty);
            s = ReUrl.Replace(s, string.Empty);
            if (s == before) break;
        }
        return s;
    }

    /// <summary>
    /// 判定一条待译文本是否属于系统噪音。
    /// 规则按"最具体 → 最宽泛"顺序短路，命中即返回。
    /// </summary>
    public static FilterDecision Classify(string content, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new FilterDecision(true, "R0", "空消息", "内容为空，无需翻译");

        // R7 放最前：乱码文本连内容判定都没意义（且会骗过中文字数统计——损坏后 CJK 计数归零）
        if (cfg.RuleMangled)
        {
            var m7 = ReMangled.Match(content);
            if (m7.Success)
                return new FilterDecision(true, "R7", "乱码兜底",
                    $"命中 {m7.Value.Length} 连'?'（DLL 编码损坏文本，翻译无意义）");
        }

        // R8 已含中文（绝对字数）：中文 ≥ 阈值即无需 EN→ZH，不看英文比例。
        // 针对 Questie 等插件播报（"Questie: [ERROR] Questie 数据库中缺少的
        // 任务 90133，请到 GitHub 或 Discord 上报告"）——内容本就是中文，
        // 模型把中文再翻成另一种中文，纯浪费。
        if (cfg.RuleChinesePresent)
        {
            var plain = StripWoWMarkup(content);
            var cjk = ReCjk.Matches(plain).Count;
            if (cjk >= cfg.ChinesePresentMinChars)
                return new FilterDecision(true, "R8", "已含中文(插件播报)",
                    $"剥离标记后含中文 {cjk} 字（≥{cfg.ChinesePresentMinChars}），本就是中文内容，EN→ZH 无事可做");
        }

        return new FilterDecision(false, "-", "需要翻译", "判定为玩家聊天，转发模型");
    }
}
