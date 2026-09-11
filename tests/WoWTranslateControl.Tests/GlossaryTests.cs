using System.IO;
using WoWTranslateControl.Core.Glossary;
using Xunit;

namespace WoWTranslateControl.Tests;

public class GlossaryApplierTests
{
    private static readonly List<GlossaryEntry> Entries = new()
    {
        new(1, "Thorns", "荆棘"),
        new(2, "Dragon's Breath", "龙息"),
        new(3, "Dragon", "龙"),
        new(4, "tank", "坦克"),
    };

    [Fact]
    public void 长术语优先_不被短术语截断()
    {
        var (text, map) = GlossaryApplier.Apply("watch out for Dragon's Breath!", Entries);
        Assert.Contains("⟦G2⟧", text);
        Assert.DoesNotContain("⟦G3⟧", text);
        Assert.Equal("龙息", map["⟦G2⟧"]);
    }

    [Fact]
    public void ph_wt_占位符_绝不参与替换()
    {
        var (text, _) = GlossaryApplier.Apply("lf tank http://ph.wt/1", Entries);
        Assert.Contains("http://ph.wt/1", text); // 占位符原样保留
        Assert.Contains("⟦G4⟧", text);           // tank 被替换
    }

    [Fact]
    public void WoW超链接_被保护_不被术语污染()
    {
        var link = "|Hitem:19019:0:0:0:0:0:0:0:0:0|h[Thunderfury]|h";
        var (text, _) = GlossaryApplier.Apply($"check {link} nice tank", Entries);
        Assert.Contains(link, text);   // 链接原样保留
        Assert.Contains("⟦G4⟧", text); // tank 被替换
    }

    [Fact]
    public void Restore_还原目标语术语()
    {
        var (_, map) = GlossaryApplier.Apply("pull like a tank please", Entries);
        var (restored, all) = GlossaryApplier.Restore("像⟦G4⟧一样拉怪", map);
        Assert.True(all);
        Assert.Equal("像坦克一样拉怪", restored);
    }

    [Fact]
    public void Restore_模型吞掉占位符时不崩溃()
    {
        var (_, map) = GlossaryApplier.Apply("tank", Entries);
        var (restored, all) = GlossaryApplier.Restore("没有占位符的译文", map);
        Assert.Equal("没有占位符的译文", restored);
        Assert.True(all);
    }

    [Fact]
    public void 大小写不敏感()
    {
        var (text, _) = GlossaryApplier.Apply("TANK needed", Entries);
        Assert.Contains("⟦G4⟧", text);
    }
}

public class GlossaryStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "glossary.json");
    }

    [Fact]
    public void 增删改_版本号自增()
    {
        var store = new GlossaryStore(TempPath());
        var v0 = store.Version;
        var e = store.Add("Thorns", "荆棘");
        Assert.Equal(v0 + 1, store.Version);
        Assert.True(store.Remove(e.Id));
        Assert.Equal(v0 + 2, store.Version);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void 重复术语_拒绝添加()
    {
        var store = new GlossaryStore(TempPath());
        store.Add("Thorns", "荆棘");
        Assert.Throws<InvalidOperationException>(() => store.Add("thorns", "刺"));
    }

    [Fact]
    public void 持久化往返()
    {
        var path = TempPath();
        var store = new GlossaryStore(path);
        store.Add("Dragon's Breath", "龙息");
        store.Add("Thorns", "荆棘");

        var reloaded = GlossaryStore.Load(path);
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(store.Version, reloaded.Version);
        Assert.Equal("龙息", reloaded.Snapshot().First(e => e.From == "Dragon's Breath").To);
    }

    [Fact]
    public void Apply_TermWithTrailingPunctuation_Matches()
    {
        // 🔴  对尾部标点失效（"M+" 的 + 后无词边界）——修复后必须用 lookaround
        var entries = new List<GlossaryEntry> { new(1, "M+", "大秘境") };
        var (text, map) = GlossaryApplier.Apply("LFM M+ key", entries);
        Assert.Contains("⟦G1⟧", text);
        Assert.Equal("大秘境", map["⟦G1⟧"]);
        var (restored, ok) = GlossaryApplier.Restore(text, map);
        Assert.True(ok);
        Assert.Contains("大秘境", restored);
    }

    [Fact]
    public void Apply_SingleLetter_OnlyWholeWord()
    {
        // 单字母术语只在整词处替换，不吃进别的单词
        var entries = new List<GlossaryEntry> { new(1, "N", "治疗") };
        var (text, _) = GlossaryApplier.Apply("LF1M N, need inc heal", entries);
        Assert.Contains("⟦G1⟧", text);
        Assert.DoesNotContain("i⟦", text.ToLower()); // inc 里的 n 不被吃
    }
}