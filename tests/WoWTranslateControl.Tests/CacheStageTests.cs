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
