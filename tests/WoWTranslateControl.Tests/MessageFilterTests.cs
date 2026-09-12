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
    [InlineData("： |cffFF2020http://ph.wt/1|r 我觉得是血")]   // 2026-09-11 游戏实测：链接占位符+纯中文
    [InlineData("： |cffFF2020http://ph.wt/1|r 但是冰莲")]
    public void R6_系统链接加中文_剥离标记后拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R6", d.RuleId);
    }

    [Theory]
    [InlineData("????? [Yuna's Bag] ????????????")]          // DLL 编码损坏产物
    [InlineData("? ???? [Nicholastang]")]                     // 游戏实测：分散 1+4 连，最大段 4 也要拦
    public void R7_乱码兜底_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R7", d.RuleId);
    }

    [Theory]
    [InlineData("what??? really???")]                        // 真实聊天连打 3 个问号，不误杀
    [InlineData("w me for invite raid new world bosses")]
    public void R7_真实聊天_放行(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.False(d.Filtered);
    }

    [Theory]
    [InlineData("Questie: [ERROR] Questie 数据库中缺少的任务 90133，请到 GitHub 或 Discord 上报告，谢谢!")] // 游戏实测：R6 比例擦边，靠 R8/R6 任一拦截即可
    [InlineData("数据库缺少任务了请到 GithubDiscordGithubDiscordGithub 上报告谢谢")]  // 纯 R8 场景：cjk 15 / latin 32，R6 比例规则放行
    public void R8_已含中文_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Contains(d.RuleId, new[] { "R6", "R8" });
    }

    [Theory]
    [InlineData("lfm 黑上 need 1 healer and 2 dps")]          // 真实组队聊天：中文仅 2 字（地名），不触发 R8
    [InlineData("wtf the boss bugged out 中?")]               // 中文 1 字，远离阈值
    public void R8_真实聊天_放行(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.False(d.Filtered);
    }

    [Theory]
    [InlineData("lf tank http://ph.wt/1")]                    // 真实玩家聊天，带占位符
    [InlineData("LFM a Tank and a DPS http://ph.wt/1")]
    [InlineData("anyone knows where the quest npc is?")]
    [InlineData("brb 中")]                                     // 极短中英混合，字母数超过 R6 阈值，放行
    [InlineData("w me for invite raid new world bosses")]     // 2026-09-11 游戏实测：R4 英文词不得误杀真实组队聊天
    [InlineData("pass me the flask pls")]                     // "pass" 单独出现不匹配 "passes on"
    public void 玩家聊天_放行(string content)
    {        var d = MessageFilter.Classify(content, Cfg());
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

public class ChannelTagDigitTests
{
    [Fact]
    public void ZH2EN标签_含数字_能解析并剥离()
    {
        var ch = MessageFilter.ParseChannelTag("\x01ZH2EN\x01测试消息", out var clean);
        Assert.Equal("ZH2EN", ch);
        Assert.Equal("测试消息", clean);
    }

    [Fact]
    public void 旧纯字母标签_仍正常解析()
    {
        var ch = MessageFilter.ParseChannelTag("\x01SAY\x01hello", out var clean);
        Assert.Equal("SAY", ch);
        Assert.Equal("hello", clean);
    }
}
