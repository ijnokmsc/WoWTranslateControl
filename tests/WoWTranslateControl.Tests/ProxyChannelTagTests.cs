using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Pipeline;
using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Models;
using WoWTranslateControl.Core;
using Xunit;

namespace WoWTranslateControl.Tests;

/// <summary>
/// 端到端：真实 ProxyServer 监听 + 模拟驱动的带频道标签请求，
/// 断言流量条目携带频道（频道过滤可观测性的根基）。
/// </summary>
public class ProxyChannelTagTests
{
    private static async Task<TrafficEntry?> PostOnceAsync(AppConfig cfg, string content)
    {
        // 与真实引擎一致：JSON 库序列化，0x01 控制字符自动转义为 \u0001
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content } }
        });
        var proxy = new WoWTranslateControl.Core.ProxyServer(cfg, new GlossaryStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wtc-tests",
                Guid.NewGuid().ToString("N"), "glossary.json")));
        var tcs = new TaskCompletionSource<TrafficEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.OnTraffic += e => tcs.TrySetResult(e);
        proxy.Start();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ = await http.PostAsync($"http://127.0.0.1:{cfg.ListenPort}/v1/chat/completions",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        finally { proxy.Stop(); }
    }

    [Fact]
    public async Task TrafficEntryCarriesChannelAndFilterBlocks()
    {
        // 综合（C1/SAY）关闭：FilterStage 先于缓存/模型拦截
        var cfg = new AppConfig { ListenPort = 18443, ChannelSay = false, CacheEnabled = false };
        var entry = await PostOnceAsync(cfg, "\u0001SAY\u0001hello friends");
        Assert.NotNull(entry);
        Assert.Equal("SAY", entry!.Channel);
        Assert.Equal(TrafficKind.Filtered, entry.Kind);
        Assert.Equal("C1", entry.RuleId);
    }

    [Fact]
    public async Task TrafficEntryCarriesChannelForEnabledChannel()
    {
        // 频道开启：到达缓存层（CacheEnabled=false → 不命中不落盘），频道仍须随行
        var cfg = new AppConfig { ListenPort = 18444, CacheEnabled = false };
        var entry = await PostOnceAsync(cfg, "\u0001GUILD\u0001anyone online?");
        Assert.NotNull(entry);
        Assert.Equal("GUILD", entry!.Channel);
    }

    [Fact]
    public async Task NumberedChannelRoutesToSynthCategory()
    {
        // CHANNEL:2 综合（含世界）：默认开启 → 放行，频道编号原样随行
        var cfg = new AppConfig { ListenPort = 18445, CacheEnabled = false };
        var entry = await PostOnceAsync(cfg, "\u0001CHANNEL:2\u0001world chat message");
        Assert.NotNull(entry);
        Assert.Equal("CHANNEL:2", entry!.Channel);
    }

    [Fact]
    public async Task NumberedChannelSynthDisabledBlocks()
    {
        // 取消勾选综合(含世界) → CHANNEL:2 被 C9 规则拦截
        var cfg = new AppConfig { ListenPort = 18446, CacheEnabled = false, ChannelSynth = false };
        var entry = await PostOnceAsync(cfg, "\u0001CHANNEL:2\u0001world chat message");
        Assert.NotNull(entry);
        Assert.Equal(TrafficKind.Filtered, entry!.Kind);
        Assert.Equal("C9", entry.RuleId);
        Assert.Equal("CHANNEL:2", entry.Channel);
    }

    [Fact]
    public async Task NumberedChannelLocalDisabledBlocks()
    {
        // 取消勾选本地防务 → CHANNEL:4 被 C10 规则拦截
        var cfg = new AppConfig { ListenPort = 18447, CacheEnabled = false, ChannelLocal = false };
        var entry = await PostOnceAsync(cfg, "\u0001CHANNEL:4\u0001local defense call");
        Assert.NotNull(entry);
        Assert.Equal(TrafficKind.Filtered, entry!.Kind);
        Assert.Equal("C10", entry.RuleId);
    }
}
