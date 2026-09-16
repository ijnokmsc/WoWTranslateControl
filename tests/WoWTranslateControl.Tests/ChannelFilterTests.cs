using System.IO;
using System.Collections.Generic;
using WoWTranslateControl.Core;
using WoWTranslateControl.Models;
using Xunit;

namespace WoWTranslateControl.Tests;

public class ChannelFilterTests
{
    private static AppConfig Cfg() => new();

    // ---- 标签解析与剥离 ----

    [Fact]
    public void TagParsedAndStripped()
    {
        var ch = MessageFilter.ParseChannelTag("\x01GUILD\x01lf tank", out var clean);
        Assert.Equal("GUILD", ch);
        Assert.Equal("lf tank", clean);
    }

    [Fact]
    public void NoTagReturnsNullAndKeepsText()
    {
        var ch = MessageFilter.ParseChannelTag("hello world", out var clean);
        Assert.Null(ch);
        Assert.Equal("hello world", clean);
    }

    [Fact]
    public void TagOnlyContentStripsToEmpty()
    {
        var ch = MessageFilter.ParseChannelTag("\x01SAY\x01", out var clean);
        Assert.Equal("SAY", ch);
        Assert.Equal("", clean);
    }

    [Fact]
    public void NullContentHandled()
    {
        var ch = MessageFilter.ParseChannelTag(null!, out var clean);
        Assert.Null(ch);
        Assert.Equal("", clean);
    }

    // ---- 频道开关判定 ----

    [Fact]
    public void DisabledChannelFilters()
    {
        var cfg = Cfg();
        cfg.ChannelFilterEnabled = true;
        cfg.ChannelGuild = false;
        var d = MessageFilter.CheckChannel("GUILD", cfg);
        Assert.NotNull(d);
        Assert.Equal("C5", d!.RuleId);
        Assert.True(d.Filtered);
    }

    [Fact]
    public void EnabledChannelPasses()
    {
        var cfg = Cfg();
        cfg.ChannelParty = true;
        Assert.Null(MessageFilter.CheckChannel("PARTY", cfg));
    }

    [Fact]
    public void MasterSwitchDisablesAll()
    {
        var cfg = Cfg();
        cfg.ChannelFilterEnabled = false;
        cfg.ChannelGuild = false; // 即使频道关了，总开关关闭也放行
        Assert.Null(MessageFilter.CheckChannel("GUILD", cfg));
    }

    [Fact]
    public void UntaggedRespectsToggle()
    {
        var cfg = Cfg();
        cfg.ChannelUntagged = false;
        var d = MessageFilter.CheckChannel(null, cfg);
        Assert.NotNull(d);
        Assert.Equal("C0", d!.RuleId);

        cfg.ChannelUntagged = true;
        Assert.Null(MessageFilter.CheckChannel(null, cfg));
    }

    [Fact]
    public void UnknownChannelPassesToContentRules()
    {
        var cfg = Cfg();
        Assert.Null(MessageFilter.CheckChannel("MONSTER_SAY", cfg));
    }

    // ---- FilterStage 集成：频道过滤先于内容规则 ----

    [Fact]
    public async Task FilterStageChannelFirst()
    {
        var cfg = Cfg();
        cfg.ChannelGuild = false;
        var stage = new WoWTranslateControl.Core.Pipeline.FilterStage(cfg);
        var ctx = new WoWTranslateControl.Core.Pipeline.TranslationContext(
            "some english text", Array.Empty<byte>(), cfg) { Channel = "GUILD" };
        var result = await stage.ProcessAsync(ctx, default);
        Assert.NotNull(result);
        Assert.Equal("C5", result!.RuleId);
    }

    // ---- Google 免费翻译响应解析 ----

    [Fact]
    public void GoogleResponseParsed()
    {
        const string json = """[[["你好","hello",null,null,10],["，世界","world",null,null,3]],null,"en",["你好"],null,null,null,[]]""";
        Assert.Equal("你好，世界", WoWTranslateControl.Core.Providers.GoogleFreeProvider.ParseGoogleResponse(json));
    }

    [Fact]
    public void GoogleResponseGarbageReturnsNull()
    {
        Assert.Null(WoWTranslateControl.Core.Providers.GoogleFreeProvider.ParseGoogleResponse("not json"));
        Assert.Null(WoWTranslateControl.Core.Providers.GoogleFreeProvider.ParseGoogleResponse("[]"));
    }

    // ---- Provider 链 ----

    [Fact]
    public void ProviderChainByMode()
    {
        Assert.Equal(new[] { "llama-local" }, WoWTranslateControl.Core.Providers.ProviderManager.BuildChain("local"));
        Assert.Equal(new[] { "openai-compat" }, WoWTranslateControl.Core.Providers.ProviderManager.BuildChain("openai"));
        Assert.Equal(new[] { "google-free" }, WoWTranslateControl.Core.Providers.ProviderManager.BuildChain("google"));
        Assert.Equal(new[] { "llama-local", "openai-compat", "google-free" },
            WoWTranslateControl.Core.Providers.ProviderManager.BuildChain("auto"));
        Assert.Equal(new[] { "llama-local" }, WoWTranslateControl.Core.Providers.ProviderManager.BuildChain("bogus"));
    }
}
