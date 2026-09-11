using System;
using System.IO;
using WoWTranslateControl.Core;
using Xunit;

namespace WoWTranslateControl.Tests;

public class DllSwitcherTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeFakeGame()
    {
        var game = TempDir();
        File.WriteAllText(Path.Combine(game, "dinput8.dll"), "fake-dinput-gs");
        File.WriteAllText(Path.Combine(game, "WoWTranslate335.dll"), "fake-gs-dll");
        File.WriteAllText(Path.Combine(game, "dlls.txt"), "WoWTranslate335.dll\n");
        return game;
    }

    private static string MakeDirectAssets()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "dinput8.dll"), "fake-dinput-direct");
        File.WriteAllText(Path.Combine(dir, "WoWTranslateDirect.dll"), "fake-direct-dll");
        return dir;
    }

    [Fact]
    public void ProbeDetectsGsTrack()
    {
        var game = MakeFakeGame();
        var st = DllSwitcher.Probe(game, Path.Combine(game, "no-assets"));
        Assert.Equal(DllSwitcher.TrackGs, st.Current);
        Assert.False(st.DirectAssetsReady);
    }

    [Fact]
    public void ProbeDetectsDisabledWhenNoDinput()
    {
        var game = TempDir();
        var st = DllSwitcher.Probe(game, Path.Combine(game, "no-assets"));
        Assert.Equal("disabled", st.Current);
    }

    [Fact]
    public void SwitchToDirectRequiresAssets()
    {
        var game = MakeFakeGame();
        var lines = DllSwitcher.SwitchTo(game, DllSwitcher.TrackDirect,
            Path.Combine(game, "no-assets"), wowRunning: () => false);
        Assert.Contains(lines, l => l.StartsWith("❌"));
        // 游戏根目录不应被动过
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslate335.dll")));
    }

    [Fact]
    public void SwitchToDirectAndBackPreservesGsBaseline()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();
        var gsContent = File.ReadAllText(Path.Combine(game, "WoWTranslate335.dll"));

        // 切到 direct
        var lines = DllSwitcher.SwitchTo(game, DllSwitcher.TrackDirect, assets, wowRunning: () => false);
        Assert.Contains(lines, l => l.StartsWith("✅"));
        Assert.Equal("fake-dinput-direct", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslateDirect.dll")));
        Assert.False(File.Exists(Path.Combine(game, "WoWTranslate335.dll")));
        Assert.Equal("WoWTranslateDirect.dll", File.ReadAllText(Path.Combine(game, "dlls.txt")).Trim());

        var st = DllSwitcher.Probe(game, assets);
        Assert.Equal(DllSwitcher.TrackDirect, st.Current);

        // GS 基线备份完好
        var backup = Path.Combine(game, "wtc_backup", "gs", "WoWTranslate335.dll");
        Assert.True(File.Exists(backup));
        Assert.Equal(gsContent, File.ReadAllText(backup));

        // 切回 gs
        var back = DllSwitcher.SwitchTo(game, DllSwitcher.TrackGs, assets, wowRunning: () => false);
        Assert.Contains(back, l => l.StartsWith("✅"));
        Assert.Equal("fake-dinput-gs", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
        Assert.Equal(gsContent, File.ReadAllText(Path.Combine(game, "WoWTranslate335.dll")));
        Assert.Equal("WoWTranslate335.dll", File.ReadAllText(Path.Combine(game, "dlls.txt")).Trim());
        Assert.Equal(DllSwitcher.TrackGs, DllSwitcher.Probe(game, assets).Current);
    }

    [Fact]
    public void SwitchToSameTrackIsNoOp()
    {
        var game = MakeFakeGame();
        var lines = DllSwitcher.SwitchTo(game, DllSwitcher.TrackGs, TempDir(), wowRunning: () => false);
        Assert.Contains(lines, l => l.Contains("已处于目标轨道"));
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslate335.dll")));
    }

    [Fact]
    public void SwitchFromDisabledToGsRestoresBackup()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();

        // gs → direct → 手动删光（模拟 Direct DLL 损坏被用户清掉）
        DllSwitcher.SwitchTo(game, DllSwitcher.TrackDirect, assets, wowRunning: () => false);
        foreach (var f in new[] { "dinput8.dll", "WoWTranslateDirect.dll", "dlls.txt" })
            File.Delete(Path.Combine(game, f));
        Assert.Equal("disabled", DllSwitcher.Probe(game, assets).Current);

        // disabled → gs：基线备份仍在，可恢复
        var lines = DllSwitcher.SwitchTo(game, DllSwitcher.TrackGs, assets, wowRunning: () => false);
        Assert.Contains(lines, l => l.StartsWith("✅"));
        Assert.Equal("fake-dinput-gs", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
        Assert.Equal(DllSwitcher.TrackGs, DllSwitcher.Probe(game, assets).Current);
    }
}
