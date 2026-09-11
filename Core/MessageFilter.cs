using System;
using System.Text.RegularExpressions;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core;

/// <summary>过滤判定结果。</summary>
public sealed record FilterDecision(bool Filtered, string RuleId, string RuleName, string Detail);

/// <summary>
/// 消息过滤引擎。
///
/// 设计依据（来自 proxy_traffic.log 的 680 条真实请求统计）：
///   战斗日志 344 条(50.6%) + 图标刷屏 184 条(27.1%) + 拾取 73 条(10.7%) + 其他播报 8 条(1.2%)
///   = 609 条(88.6%) 属于系统噪音，只有 71 条(10.4%) 是真正需要 EN→ZH 翻译的玩家聊天。
///
/// 核心思路：把插件的"漏洞"反转成"指纹"。
///   插件 WoWTranslate.lua 的 FindAllProtectedSpans 在遇到嵌套/未闭合的魔兽超链接时会失效，
///   导致 |Hunit:0x...:Brick|h、|TInterface\TargetingFrame\UI-RaidTargetingIcon_2.blp:0|t
///   这类原始标记以纯文本形式泄漏进请求。而正常玩家聊天的链接会被正确替换成
///   http://ph.wt/N 占位符。因此"请求里出现原始标记"本身就强烈指向系统播报。
/// </summary>
public static class MessageFilter
{
    // ---- R1 团队图标标记刷屏：日志占比 27.1%，仅 10 条唯一内容却重复 184 次(18.4x) ----
    private static readonly Regex ReIcon = new(
        @"RaidTargetingIcon|\|Hicon:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---- R2 SPELL_* 战斗日志：日志占比 50.6%，最大单一类别 ----
    private static readonly Regex ReSpell = new(
        @"\bSPELL_[A-Z][A-Z_]*\b|\|Hspell:",
        RegexOptions.Compiled);

    // ---- R3 其他战斗事件（近战/远程/环境伤害） ----
    private static readonly Regex ReCombatOther = new(
        @"\b(?:SWING|RANGE|ENVIRONMENTAL)_[A-Z][A-Z_]*\b",
        RegexOptions.Compiled);

    // ---- R4 拾取/分配/制造播报：日志占比 10.7% ----
    //
    // 重要：这里刻意不包含 http://ph.wt/ 和 |Hitem: 之类"通用链接占位符"。
    // 实测教训：ph.wt 是插件替换【所有】超链接的占位符，玩家聊天里发物品链接同样会带
    // （如 "： lf tank http://ph.wt/1"、"： LFM a Tank and a DPS http://ph.wt/1"）。
    // 一旦把它当过滤关键词，真实英文聊天会被误杀，游戏里就看不到译文了。
    // 因此 R4 只依据系统播报固定句式判定 —— 这些句式只出现在系统播报里。
    // 2026-09-11 游戏实测补充：英文客户端的拾取播报是英文固定句式
    //   （"X automatically passes on [item], because X cannot loot that item."），
    //   中文关键词根本匹配不上，导致整队放弃拾取刷屏全部送模型。补英文句式。
    private static readonly Regex ReLoot = new(
        @"获得了物品|赢得了|放弃了|拾取了|贪婪|需求|掷点|制造了|分解了|" +
        @"被解散了|获得了\s*\d+\s*点|本次售卖共获利|" +
        @"automatically passes on|cannot loot that item|cannot collect that item|" +
        @"receive[sd]? loot|wins? the roll|won the roll|rolls? (Need|Greed)|" +
        @"need before greed|may roll for",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---- R5 伤害/死亡/表情/经验播报 ----
    // 覆盖各种伤害类型（物理/掉落/火焰/…）与近战命中播报。
    private static readonly Regex ReDamageDeath = new(
        @"点物理伤害|点掉落伤害|点火焰伤害|点冰霜伤害|点自然伤害|点暗影伤害|点神圣伤害|" +
        @"点奥术伤害|点伤害|近战攻击命中|攻击被|" +
        @"吸收了|杀死了|被杀死|死亡了|你的宠物|获得了\s*\d+\s*点经验|" +
        @"挥了挥手|大笑|坐下|跳舞|招手|开始施法|施放",
        RegexOptions.Compiled);

    private static readonly Regex ReMarkupH = new(@"\|H[^|]*\|h", RegexOptions.Compiled);
    private static readonly Regex ReMarkupT = new(@"\|T[^|]*\|t", RegexOptions.Compiled);
    private static readonly Regex ReMarkupColor = new(@"\|c[0-9a-fA-F]{8}|\|r|\|h", RegexOptions.Compiled);
    private static readonly Regex ReUrl = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ReCjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex ReLatin = new(@"[A-Za-z]", RegexOptions.Compiled);

    // ---- 频道标签：插件 Lua 补丁在文本前加 \1<频道>\1，控制台无条件剥离并据此过滤 ----
    private static readonly Regex ReChannelTag =
        new(@"^\x01([A-Z_]+)\x01", RegexOptions.Compiled);

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
    /// 在内容规则（R0-R6）之前执行：频道不要的消息连内容判定都不必做。
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

        if (cfg.RuleIconSpam)
        {
            var m = ReIcon.Match(content);
            if (m.Success)
                return new FilterDecision(true, "R1", "图标刷屏",
                    $"命中标记 {m.Value}（团队标记图标，无自然语言）");
        }

        if (cfg.RuleSpellLog)
        {
            var m = ReSpell.Match(content);
            if (m.Success)
                return new FilterDecision(true, "R2", "战斗日志",
                    $"命中事件 {m.Value}（技能/Buff 播报）");
        }

        if (cfg.RuleCombatOther)
        {
            var m = ReCombatOther.Match(content);
            if (m.Success)
                return new FilterDecision(true, "R3", "其他战斗事件",
                    $"命中事件 {m.Value}");
        }

        if (cfg.RuleLoot)
        {
            var m = ReLoot.Match(content);
            if (m.Success)
                return new FilterDecision(true, "R4", "拾取播报",
                    $"命中关键词 {m.Value}");
        }

        if (cfg.RuleDamageDeath)
        {
            var m = ReDamageDeath.Match(content);
            if (m.Success)
                return new FilterDecision(true, "R5", "伤害死亡播报",
                    $"命中关键词 {m.Value}");
        }

        // R6 中文兜底：剥离标记后中文占主导，说明本来就是中文，EN→ZH 无事可做。
        // 这是启发式规则，故单独提供开关与可调阈值。
        if (cfg.RuleChineseOnly)
        {
            var plain = StripWoWMarkup(content);
            var cjk = ReCjk.Matches(plain).Count;
            var latin = ReLatin.Matches(plain).Count;
            if (cjk >= 1 && latin <= cjk * cfg.ChineseRatioLimit)
                return new FilterDecision(true, "R6", "中文为主(兜底)",
                    $"剥离标记后 中文{cjk}字 / 英文{latin}字母，无需翻译");
        }

        return new FilterDecision(false, "-", "需要翻译", "判定为玩家聊天，转发模型");
    }
}
