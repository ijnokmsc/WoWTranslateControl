using System.IO;
using WoWTranslateControl.Core;
using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Models;
using Xunit;

namespace WoWTranslateControl.Tests;

public class CacheStageTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "cache.json");
    }

    private static AppConfig Cfg() => new() { CacheEnabled = true, CachePersist = true };

    private static GlossaryStore NewGlossary() =>
        new(TempPath().Replace("cache.json", "glossary.json"));

    [Fact]
    public async Task 术语表版本变化_缓存key随之变化()
    {
        var glossary = NewGlossary();
        var cache = new CacheStage(Cfg(), glossary, TempPath(), "llama-local");

        var ctx1 = new TranslationContext("hello world", Array.Empty<byte>(), Cfg());
        await cache.ProcessAsync(ctx1, default);
        var key1 = ctx1.CacheKey;

        glossary.Add("Thorns", "荆棘");

        var ctx2 = new TranslationContext("hello world", Array.Empty<byte>(), Cfg());
        await cache.ProcessAsync(ctx2, default);
        var key2 = ctx2.CacheKey;

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task Put之后_同key命中()
    {
        var glossary = NewGlossary();
        var cache = new CacheStage(Cfg(), glossary, TempPath(), "llama-local");

        var ctx = new TranslationContext("lf tank", Array.Empty<byte>(), Cfg());
        var miss = await cache.ProcessAsync(ctx, default);
        Assert.Null(miss);

        cache.Put(ctx, "找坦克");

        var ctx2 = new TranslationContext("lf tank", Array.Empty<byte>(), Cfg());
        var hit = await cache.ProcessAsync(ctx2, default);
        Assert.NotNull(hit);
        Assert.Equal(TrafficKind.CacheHit, hit!.Kind);
        Assert.Equal("找坦克", hit.Content);
    }

    [Fact]
    public async Task 持久化_重启后仍命中()
    {
        var path = TempPath();
        var glossary = NewGlossary();
        var cfg = Cfg();

        var cache1 = new CacheStage(cfg, glossary, path, "llama-local");
        var ctx = new TranslationContext("hello", Array.Empty<byte>(), cfg);
        await cache1.ProcessAsync(ctx, default);
        cache1.Put(ctx, "你好");
        cache1.SaveNow();
        cache1.Dispose();

        var cache2 = new CacheStage(cfg, glossary, path, "llama-local");
        var ctx2 = new TranslationContext("hello", Array.Empty<byte>(), cfg);
        var hit = await cache2.ProcessAsync(ctx2, default);
        Assert.NotNull(hit);
        Assert.Equal("你好", hit!.Content);
    }

    [Fact]
    public async Task 关闭缓存_完全不命中()
    {
        var glossary = NewGlossary();
        var cfg = Cfg();
        cfg.CacheEnabled = false;
        var cache = new CacheStage(cfg, glossary, TempPath(), "llama-local");

        var ctx = new TranslationContext("hello", Array.Empty<byte>(), cfg);
        cache.Put(ctx, "你好");
        var result = await cache.ProcessAsync(ctx, default);
        Assert.Null(result);
    }
}

public class OutgoingRoutingTests
{
    private static AppConfig Cfg() => new() { CacheEnabled = true, CachePersist = false };

    [Fact]
    public async Task 外发请求_中文正文不触发内容过滤()
    {
        // R8"已含中文"对普通请求必拦；外发请求必须放行（否则中文→英文全被吞）
        var cfg = Cfg();
        var filter = new FilterStage(cfg);

        var normal = new TranslationContext("这是一个超过八个字的中文消息测试", Array.Empty<byte>(), cfg);
        var normalResult = await filter.ProcessAsync(normal, default);
        Assert.NotNull(normalResult); // R8 拦截

        var outgoing = new TranslationContext("这是一个超过八个字的中文消息测试", Array.Empty<byte>(), cfg)
        {
            Outgoing = true,
            SourceLang = "zh",
            TargetLang = "en",
            Channel = "ZH2EN",
        };
        var outgoingResult = await filter.ProcessAsync(outgoing, default);
        Assert.Null(outgoingResult); // 放行进 Provider
    }

    [Fact]
    public async Task 外发请求_缓存key与普通方向分离()
    {
        var glossary = new GlossaryStore(Path.Combine(
            Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"), "glossary.json"));
        var cache = new CacheStage(Cfg(), glossary, Path.Combine(
            Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"), "cache.json"), "llama-local");

        var inc = new TranslationContext("测试", Array.Empty<byte>(), Cfg());
        await cache.ProcessAsync(inc, default);
        var outCtx = new TranslationContext("测试", Array.Empty<byte>(), Cfg())
        {
            Outgoing = true,
            TargetLang = "en",
        };
        await cache.ProcessAsync(outCtx, default);

        Assert.NotEqual(inc.CacheKey, outCtx.CacheKey);
    }
}
