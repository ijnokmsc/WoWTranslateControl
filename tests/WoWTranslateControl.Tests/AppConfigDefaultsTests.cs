using System;
using System.IO;
using System.Text.Json;
using WoWTranslateControl.Models;
using Xunit;

namespace WoWTranslateControl.Tests;

/// <summary>v25 便携化默认值：新用户零配置解压即用。</summary>
public class AppConfigDefaultsTests
{
    [Fact]
    public void LlamaDir默认_在运行目录下的llama_cpp()
    {
        var cfg = new AppConfig();
        var expected = Path.Combine(AppContext.BaseDirectory, "llama.cpp");
        Assert.Equal(expected, cfg.LlamaDir);
    }

    [Fact]
    public void Threads默认_为逻辑核心数一半且夹取范围()
    {
        var expected = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
        var cfg = new AppConfig();
        Assert.Equal(expected, cfg.Threads);
    }

    [Fact]
    public void Threads显式设置_序列化往返保留()
    {
        var cfg = new AppConfig { Threads = 7 };
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(7, back.Threads);
    }

    [Fact]
    public void 旧配置文件里的D盘llama目录_加载后不被覆盖()
    {
        // 旧 settings.json 里存的 "D:\llama.cpp"（JSON 转义）加载后应原样保留
        var b = Convert.ToChar(92);
        var json = "{\"LlamaDir\":\"D:" + b + b + "llama.cpp\"}";
        var loaded = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("D:" + b + "llama.cpp", loaded.LlamaDir);
    }
}
