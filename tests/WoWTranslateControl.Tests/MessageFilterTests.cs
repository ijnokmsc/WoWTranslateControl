using WoWTranslateControl.Core;
using WoWTranslateControl.Models;
using Xunit;

namespace WoWTranslateControl.Tests;

/// <summary>R0-R6 过滤规则回归（基线：680 条真实流量统计）。</summary>
public class MessageFilterTests
{
    private static AppConfig Cfg() => new()
    {
        RuleIconSpam = true,
        RuleSpellLog = true,
        RuleCombatOther = true,
        RuleLoot = true,
        RuleDamageDeath = true,
        RuleChineseOnly = true,
        ChineseRatioLimit = 2.0,
    };

    [Theory]
    [InlineData("|Hicon:0x1:Brick|h")]
    [InlineData("|TInterface\\TargetingFrame\\UI-RaidTargetingIcon_2.blp:0|t Brick")]
    public void R1_图标刷屏_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R1", d.RuleId);
    }

    [Theory]
    [InlineData("SPELL_AURA_APPLIED Brick gains Rejuvenation")]
    [InlineData("SPELL_PERIODIC_DAMAGE 123")]
    public void R2_战斗日志_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R2", d.RuleId);
    }

    [Theory]
    [InlineData("SWING_MISSED Brick")]
    [InlineData("ENVIRONMENTAL_DAMAGE_FALLING")]
    public void R3_其他战斗事件_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R3", d.RuleId);
    }

    [Theory]
    [InlineData("你获得了物品：雷霆之剑")]
    [InlineData("Brick 赢得了 掷点 88 (需求)")]
    [InlineData("Runetang automatically passes on Yuna's Bag because she cannot loot that item")]
    [InlineData("Manahorn automatically passes on Yuna's Bag, because he cannot loot that item.")]
    [InlineData("You receive loot: 雷霆之剑")]
    [InlineData("Brick wins the roll 88 vs 12")]
    [InlineData("Calyi rolls Greed 34")]
    public void R4_拾取播报_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R4", d.RuleId);
    }

    [Fact]
    public void R6_中文为主_被兜底拦截()
    {
        var d = MessageFilter.Classify("你好，这里怎么走？", Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R6", d.RuleId);
    }

    [Theory]
    [InlineData("lf tank http://ph.wt/1")]                    // 真实玩家聊天，带占位符
    [InlineData("LFM a Tank and a DPS http://ph.wt/1")]
    [InlineData("anyone knows where the quest npc is?")]
    [InlineData("brb 中")]                                     // 极短中英混合，字母数超过 R6 阈值，放行
    [InlineData("w me for invite raid new world bosses")]     // 2026-09-11 游戏实测：R4 英文词不得误杀真实组队聊天
    [InlineData("pass me the flask pls")]                     // "pass" 单独出现不匹配 "passes on"
    public void 玩家聊天_放行(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.False(d.Filtered);
    }

    [Fact]
    public void R0_空消息_被拦截()
    {
        var d = MessageFilter.Classify("  ", Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R0", d.RuleId);
    }
}
