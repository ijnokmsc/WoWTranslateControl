using System.Linq;
using WoWTranslateControl.Core.Hardware;
using Xunit;

namespace WoWTranslateControl.Tests;

public class ModelAdvisorTests
{
    /// <summary>真实资产命名样本（b10903，2026-09-11 实测）；另含 latest 空壳 release（v0.4.0 无资产）。</summary>
    private const string ReleasesJson = """
    [
      { "tag_name": "v0.4.0", "assets": [] },
      { "tag_name": "b10903", "assets": [
        { "name": "llama-b10903-bin-win-cuda-12.4-x64.zip", "browser_download_url": "https://example/llama-b10903-bin-win-cuda-12.4-x64.zip" },
        { "name": "cudart-llama-bin-win-cuda-12.4-x64.zip", "browser_download_url": "https://example/cudart-llama-bin-win-cuda-12.4-x64.zip" },
        { "name": "llama-b10903-bin-win-cpu-x64.zip", "browser_download_url": "https://example/llama-b10903-bin-win-cpu-x64.zip" },
        { "name": "llama-b10903-bin-win-cpu-arm64.zip", "browser_download_url": "https://example/llama-b10903-bin-win-cpu-arm64.zip" },
        { "name": "llama-b10903-bin-ubuntu-x64.zip", "browser_download_url": "https://example/llama-b10903-bin-ubuntu-x64.zip" },
        { "name": "llama-b10903.exe", "browser_download_url": "https://example/llama-b10903.exe" }
      ] }
    ]
    """;

    [Fact]
    public void ExtractBuildsSkipsEmptyLatestAndNonWin()
    {
        var builds = ModelAdvisor.ExtractBuilds(ReleasesJson);
        // 跳过空壳 v0.4.0，取 b10903；排除 ubuntu 与 exe；arm64 zip 无 cuda/cpu/vulkan 关键词？含 cpu → 保留
        Assert.Equal(4, builds.Count);
        Assert.DoesNotContain(builds, b => b.AssetName.Contains("ubuntu"));
        Assert.DoesNotContain(builds, b => b.AssetName.EndsWith(".exe"));
        Assert.Contains(builds, b => b.AssetName == "llama-b10903-bin-win-cuda-12.4-x64.zip");
        Assert.Contains(builds, b => b.AssetName == "cudart-llama-bin-win-cuda-12.4-x64.zip");
    }

    [Fact]
    public void ExtractBuildsReturnsEmptyForNoWinZips()
    {
        const string json = """[ { "tag_name": "v0.4.0", "assets": [] } ]""";
        Assert.Empty(ModelAdvisor.ExtractBuilds(json));
    }

    [Fact]
    public void OrderBuildsPutsRecommendedFirst()
    {
        var builds = ModelAdvisor.ExtractBuilds(ReleasesJson);
        var rec = ModelAdvisor.Recommend(new GpuInfo("RTX Test", 8192, true), 32);
        var ordered = ModelAdvisor.OrderBuilds(builds, rec);
        Assert.StartsWith("llama-", ordered[0].AssetName);
        Assert.Contains("cuda", ordered[0].AssetName);
        // cudart 排在主程序之后
        Assert.DoesNotContain("cudart", ordered[0].AssetName);
    }

    [Fact]
    public void OrderBuildsPutsX64BeforeArm64ForCpuRec()
    {
        var builds = ModelAdvisor.ExtractBuilds(ReleasesJson);
        var rec = ModelAdvisor.Recommend(new GpuInfo("Intel UHD", 0, false), 32);
        var ordered = ModelAdvisor.OrderBuilds(builds, rec);
        Assert.Contains("cpu-x64", ordered[0].AssetName);
        Assert.DoesNotContain("arm64", ordered[0].AssetName);
    }

    [Fact]
    public void RecommendPrefersHyMt2Models()
    {
        var cpuRec = ModelAdvisor.Recommend(new GpuInfo("Intel UHD", 0, false), 32);
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M.gguf", cpuRec.Model.FileName);
        Assert.Equal("bin-win-cpu", cpuRec.LlamaAssetContains);

        var gpuRec = ModelAdvisor.Recommend(new GpuInfo("RTX 4070", 12288, true), 32);
        Assert.Equal("Hy-MT2-7B-Q4_K_M.gguf", gpuRec.Model.FileName);
        Assert.Equal("bin-win-cuda", gpuRec.LlamaAssetContains);

        // 模型清单必须包含 Hy-MT2 两个档位
        Assert.Contains(ModelAdvisor.Models, m => m.FileName == "Hy-MT2-1.8B-Q4_K_M.gguf");
        Assert.Contains(ModelAdvisor.Models, m => m.FileName == "Hy-MT2-7B-Q4_K_M.gguf");
    }
}
