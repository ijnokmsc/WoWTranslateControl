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
    public void ph_wt_占位符_送模型前必须保护()
    {
        // 2026-09-11 游戏实测：模型看到 http://ph.wt/1 会篡改，保护段必须穿透模型调用
        var (text, map) = GlossaryApplier.Apply("lf tank http://ph.wt/1", Entries);
        Assert.Contains("⟦P0⟧", text);
        Assert.DoesNotContain("http://ph.wt/1", text); // 模型不能看到原始占位符
        Assert.Contains("⟦G4⟧", text);                 // tank 被替换
        Assert.Equal("http://ph.wt/1", map["⟦P0⟧"]);
    }

    [Fact]
    public void WoW超链接_被保护_不被术语污染()
    {
        var link = "|Hitem:19019:0:0:0:0:0:0:0:0:0|h[Thunderfury]|h";
        var (text, map) = GlossaryApplier.Apply($"check {link} nice tank", Entries);
        Assert.DoesNotContain("|Hitem:", text);  // 模型看不到标记
        Assert.Contains("⟦P0⟧", text);
        Assert.Contains("⟦G4⟧", text);
        Assert.Equal(link, map["⟦P0⟧"]);
    }

    [Fact]
    public void 包裹链接_整体摘成一个保护段()
    {
        // 游戏实测样本：|cff...DDhttp://ph.wt/1|r 是插件给物品链接的完整包装，
        // 若按零散形态摘成 |cff0070DD⟦P0⟧|r 三段，模型仍会篡改碎片
        var (text, map) = GlossaryApplier.Apply("|cff0070DDhttp://ph.wt/1|r ya", Entries);
        Assert.Equal("⟦P0⟧ ya", text);
        Assert.Equal("|cff0070DDhttp://ph.wt/1|r", map["⟦P0⟧"]);
    }

    [Fact]
    public void 保护段_穿透模型后完整还原()
    {
        var (text, map) = GlossaryApplier.Apply("|cff0070DDhttp://ph.wt/1|r ya", Entries);
        Assert.Equal("⟦P0⟧ ya", text);
        // 模拟模型只翻译了 ya
        var (restored, all) = GlossaryApplier.Restore("⟦P0⟧ 好", map);
        Assert.True(all);
        Assert.Equal("|cff0070DDhttp://ph.wt/1|r 好", restored);
    }

    [Fact]
    public void 词表关闭时_保护段依然生效()
    {
        var (text, map) = GlossaryApplier.Apply("check http://ph.wt/2", null!);
        Assert.Equal("check ⟦P0⟧", text);
        Assert.Equal("http://ph.wt/2", map["⟦P0⟧"]);
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
public class BracketTagProtectionTests
{
    [Fact]
    public void 全大写方括号短标签_原样穿透不被翻译()
    {
        var (text, map) = GlossaryApplier.Apply("[HC] doing stuff in game", NoEntries());
        Assert.Contains("⟦P", text);
        Assert.DoesNotContain("[HC]", text);
        var (restored, all) = GlossaryApplier.Restore(text + " 已翻译", map);
        Assert.Contains("[HC]", restored);
        Assert.True(all);
    }

    [Fact]
    public void 小写单词括号_不受保护_照常送翻()
    {
        var (text, _) = GlossaryApplier.Apply("[what] doing stuff", NoEntries());
        Assert.DoesNotContain("⟦P", text);
        Assert.Contains("[what]", text);
    }

    private static IReadOnlyList<GlossaryEntry> NoEntries() =>
        Array.Empty<GlossaryEntry>();
}

public class HallucinatedPlaceholderTests
{
    [Fact]
    public void 模型无中生有的占位符_被清除()
    {
        // v24 实测：小模型从系统提示词示例抄来 ⟦G12⟧，map 里没有 → 显示给玩家
        var map = new Dictionary<string, string> { ["⟦P0⟧"] = "[物品链接]", ["⟦G33⟧"] = "每秒伤害" };
        var (restored, all) = GlossaryApplier.Restore("⟦G12⟧ ⟦P0⟧⟦G33⟧", map);
        Assert.DoesNotContain("⟦", restored);
        Assert.Contains("[物品链接]", restored);
        Assert.Contains("每秒伤害", restored);
        Assert.True(all); // 幻觉占位符已被清理，无残留
    }

    [Fact]
    public void 占位符内带空格_仍能救回()
    {
        var map = new Dictionary<string, string> { ["⟦G3⟧"] = "谢谢" };
        var (restored, all) = GlossaryApplier.Restore("⟦ G3 ⟧", map);
        Assert.Equal("谢谢", restored);
        Assert.True(all);
    }
}
