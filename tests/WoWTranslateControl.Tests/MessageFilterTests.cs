using WoWTranslateControl.Core;
using WoWTranslateControl.Models;
using Xunit;

namespace WoWTranslateControl.Tests;

/// <summary>内部质检规则回归（3.0：R1-R6 用户内容规则已移除，仅 R0/R7/R8）。</summary>
public class MessageFilterTests
{
    private static AppConfig Cfg() => new();

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
    [InlineData("Questie: [ERROR] Questie 数据库中缺少的任务 90133，请到 GitHub 或 Discord 上报告，谢谢!")]
    [InlineData("数据库缺少任务了请到 GithubDiscordGithubDiscordGithub 上报告谢谢")]
    public void R8_已含中文_被拦截(string content)
    {
        var d = MessageFilter.Classify(content, Cfg());
        Assert.True(d.Filtered);
        Assert.Equal("R8", d.RuleId);
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
    [InlineData("brb 中")]                                     // 极短中英混合，放行（Direct 驱动侧另有 CJK 跳过）
    [InlineData("w me for invite raid new world bosses")]
    [InlineData("pass me the flask pls")]
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
