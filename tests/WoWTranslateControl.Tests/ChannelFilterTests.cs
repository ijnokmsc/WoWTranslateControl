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

    // ---- 一键配置：SV 重写（纯函数） ----

    private const string SvSample = """
        WoWTranslateDB = {
        	["provider"] = "google_free",
        	["enabled"] = true,
        	["incomingFromLang"] = "en",
        	["incomingChannels"] = {
        		["PARTY"] = true,
        		["SAY"] = true,
        	},
        	["openaiEndpoint"] = "https://api.openai.com/v1/chat/completions",
        	["translateMode"] = "manual",
        	["outgoingChannels"] = {
        		["SAY"] = true,
        	},
        }
        """;

    [Fact]
    public void SavedVariablesRewriteTargetsKeys()
    {
        var t = PluginConfigurator.RewriteSavedVariables(SvSample, 8080, false, null);
        Assert.Contains("[\"provider\"] = \"openai\"", t);
        Assert.Contains("[\"openaiEndpoint\"] = \"http://127.0.0.1:8080/v1/chat/completions\"", t);
        Assert.Contains("[\"openaiModel\"] = \"WoWTranslateControl\"", t);
        Assert.Contains("[\"translateMode\"] = \"auto\"", t);
        // 未请求同步时频道块不动
        Assert.Contains("[\"incomingChannels\"] = {\n\t\t[\"PARTY\"] = true,", t.Replace("\r", ""));
    }

    [Fact]
    public void SavedVariablesRewriteSyncsIncomingChannelsOnly()
    {
        var toggles = new Dictionary<string, bool> { ["SAY"] = false, ["PARTY"] = true };
        var t = PluginConfigurator.RewriteSavedVariables(SvSample, 8080, true, toggles);
        // incomingChannels 块内 SAY 被改 false
        var incStart = t.IndexOf("[\"incomingChannels\"]");
        var outStart = t.IndexOf("[\"outgoingChannels\"]");
        var incomingBlock = t[incStart..outStart];
        Assert.Contains("[\"SAY\"] = false", incomingBlock);
        // outgoingChannels 块不受影响
        var outgoingBlock = t[outStart..];
        Assert.DoesNotContain("[\"SAY\"] = false", outgoingBlock);
    }

    [Fact]
    public void IncomingChannelRewriteHandlesMissingBlock()
    {
        var toggles = new Dictionary<string, bool> { ["SAY"] = false };
        var t = PluginConfigurator.RewriteIncomingChannels("no block here", toggles);
        Assert.Equal("no block here", t);
    }

    // ---- Lua 频道标签补丁（幂等 + 写后自检） ----

    [Fact]
    public void LuaPatchInjectsTagAndIsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var luaPath = Path.Combine(dir, "WoWTranslate.lua");
        var src = "local x = 1\nWoWTranslate_API.Translate(textToTranslate, function(translation, err) end)\n";
        File.WriteAllText(luaPath, src);

        var lines = new List<string>();
        PluginConfigurator.PatchLuaChannelTag(luaPath, lines);

        var patched = File.ReadAllText(luaPath);
        Assert.Contains("\"\\1\" .. (currentIncomingChannel or \"OTHER\") .. \"\\1\" .. textToTranslate", patched);
        Assert.True(File.Exists(Directory.GetFiles(dir, "WoWTranslate.lua.bak_*")[0]));

        var lines2 = new List<string>();
        PluginConfigurator.PatchLuaChannelTag(luaPath, lines2);
        Assert.Contains("已带频道标签补丁，跳过", string.Join("\n", lines2));
        // 二次不改变内容
        Assert.Equal(patched, File.ReadAllText(luaPath));
    }

    [Fact]
    public void LuaPatchSkipsWhenCallSiteAmbiguous()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var luaPath = Path.Combine(dir, "WoWTranslate.lua");
        var src = "WoWTranslate_API.Translate(textToTranslate, function(translation, err) end)\n" +
                  "WoWTranslate_API.Translate(textToTranslate, function(translation, err) end)\n";
        File.WriteAllText(luaPath, src);

        var lines = new List<string>();
        PluginConfigurator.PatchLuaChannelTag(luaPath, lines);
        Assert.Contains("匹配数异常", string.Join("\n", lines));
        Assert.Equal(src, File.ReadAllText(luaPath)); // 未动文件
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
