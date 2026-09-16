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

    /// <summary>模拟已部署老 GS 插件的客户端根目录（3.0 部署后 GS DLL 应保留在磁盘上、插件自然失活）。</summary>
    private static string MakeFakeGame()
    {
        var game = TempDir();
        // Wow.exe 目录门禁：假客户端根目录必须带 Wow.exe
        File.WriteAllText(Path.Combine(game, "Wow.exe"), "fake-wow");
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
    public void ProbeDetectsDisabledWhenNoDinput()
    {
        var game = TempDir();
        var st = DllSwitcher.Probe(game, Path.Combine(game, "no-assets"));
        Assert.Equal("disabled", st.Current);
    }

    [Fact]
    public void DeployDirectRequiresAssets()
    {
        var game = MakeFakeGame();
        var lines = DllSwitcher.DeployDirect(game,
            Path.Combine(game, "no-assets"), wowRunning: () => false);
        Assert.Contains(lines, l => l.StartsWith("❌"));
        // 游戏根目录不应被动过（含老 GS 插件文件）
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslate335.dll")));
        Assert.Equal("fake-dinput-gs", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
    }

    [Fact]
    public void DeployDirectOverwritesEntryAndLeavesGsDllInert()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();
        var gsContent = File.ReadAllText(Path.Combine(game, "WoWTranslate335.dll"));

        var lines = DllSwitcher.DeployDirect(game, assets, wowRunning: () => false);
        Assert.Contains(lines, l => l.StartsWith("✅"));
        // dinput8 入口被 Direct 覆盖 → WoWTranslate335.dll 无人加载（文件保留但不属于轨道清单）
        Assert.Equal("fake-dinput-direct", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslateDirect.dll")));
        Assert.Equal("WoWTranslateDirect.dll", File.ReadAllText(Path.Combine(game, "dlls.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(game, "WoWTranslate335.dll")));
        Assert.Equal(gsContent, File.ReadAllText(Path.Combine(game, "WoWTranslate335.dll")));

        var st = DllSwitcher.Probe(game, assets);
        Assert.Equal(DllSwitcher.TrackDirect, st.Current);
    }

    [Fact]
    public void DeployDirectIsIdempotent()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();
        Assert.Contains(DllSwitcher.DeployDirect(game, assets, wowRunning: () => false), l => l.StartsWith("✅"));
        // 重复部署 = 幂等重部署（不报错，结果一致）
        Assert.Contains(DllSwitcher.DeployDirect(game, assets, wowRunning: () => false), l => l.StartsWith("✅"));
        Assert.Equal("fake-dinput-direct", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
    }

    [Fact]
    public void DeployDirectRefusesWhileWowRunning()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();
        var lines = DllSwitcher.DeployDirect(game, assets, wowRunning: () => true);
        Assert.Contains(lines, l => l.Contains("Wow.exe 正在运行"));
        Assert.Equal("fake-dinput-gs", File.ReadAllText(Path.Combine(game, "dinput8.dll")));
    }

    [Fact]
    public void DeployDirectWritesDirectConfig()
    {
        var game = MakeFakeGame();
        var assets = MakeDirectAssets();
        DllSwitcher.DeployDirect(game, assets, wowRunning: () => false, listenPort: 8123);
        var cfg = Path.Combine(game, "WoWTranslateDirect.json");
        Assert.True(File.Exists(cfg));
        Assert.Contains("127.0.0.1:8123", File.ReadAllText(cfg));
    }
}

public class EnsureUpToDateTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wtc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeDirectDeployed()
    {
        var game = TempDir();
        File.WriteAllText(Path.Combine(game, "Wow.exe"), "fake-wow");
        File.WriteAllText(Path.Combine(game, "dinput8.dll"), "deployed-dinput");
        File.WriteAllText(Path.Combine(game, "WoWTranslateDirect.dll"), "deployed-engine");
        File.WriteAllText(Path.Combine(game, "dlls.txt"), "WoWTranslateDirect.dll\n");
        return game;
    }

    private static string MakeDirectAssets()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "dinput8.dll"), "deployed-dinput");
        File.WriteAllText(Path.Combine(dir, "WoWTranslateDirect.dll"), "deployed-engine");
        return dir;
    }

    [Fact]
    public void 版本漂移_自动重新部署()
    {
        var game = MakeDirectDeployed();
        File.WriteAllText(Path.Combine(game, "WoWTranslateDirect.dll"), "old-version");
        var assets = MakeDirectAssets();

        var lines = DllSwitcher.EnsureUpToDate(game, assets, wowRunning: () => false);

        Assert.Contains(lines, l => l.Contains("已自动重新部署"));
        Assert.Equal("deployed-engine", File.ReadAllText(Path.Combine(game, "WoWTranslateDirect.dll")));
    }

    [Fact]
    public void 版本一致_无动作()
    {
        var game = MakeDirectDeployed();
        var assets = MakeDirectAssets();

        var lines = DllSwitcher.EnsureUpToDate(game, assets, wowRunning: () => false);

        Assert.Empty(lines);
        Assert.Equal("deployed-engine", File.ReadAllText(Path.Combine(game, "WoWTranslateDirect.dll")));
    }

    [Fact]
    public void 游戏运行中_跳过校验()
    {
        var game = MakeDirectDeployed();
        File.WriteAllText(Path.Combine(game, "WoWTranslateDirect.dll"), "old-version");
        var assets = MakeDirectAssets();

        var lines = DllSwitcher.EnsureUpToDate(game, assets, wowRunning: () => true);

        Assert.Contains(lines, l => l.Contains("跳过"));
        Assert.Equal("old-version", File.ReadAllText(Path.Combine(game, "WoWTranslateDirect.dll")));
    }
}
