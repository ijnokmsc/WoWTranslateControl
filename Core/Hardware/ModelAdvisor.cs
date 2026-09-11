using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WoWTranslateControl.Core.Hardware;

public sealed record LlamaBuildInfo(string AssetName, string Url, string Label);

public sealed record ModelInfo(string FileName, string Url, long SizeBytes, string Label);

/// <summary>
/// 推荐引擎（需求 9）：
///   - GPU：NVIDIA 且显存足够 → CUDA 版 llama.cpp + 7B/3B 量化模型（模型全进显存）；
///   - 无 NVIDIA → CPU(AVX2) 版 + 小模型（1.5B/3B 按内存定）；
///   - llama.cpp 构建从 GitHub 最新 Release 实时取资产清单（版本号不硬编码，避免发布规律变化）；
///   - 模型清单固定指向 hf-mirror（国内可达，ADR-006 镜像优先）。
/// </summary>
public static class ModelAdvisor
{
    public sealed record Recommendation(string Summary, string LlamaAssetContains, string CpuAssetContains, ModelInfo Model);

    private const string HfMirror = "https://hf-mirror.com";

    /// <summary>
    /// 模型清单（前两位 = 腾讯混元专用翻译模型 Hy-MT2，用户现役档，翻译质量优于通用 Qwen）。
    /// 大小来自 hf-mirror 仓库实测（2026-09-11）。
    /// </summary>
    public static readonly ModelInfo[] Models =
    {
        new("Hy-MT2-1.8B-Q4_K_M.gguf",
            $"{HfMirror}/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf",
            1_133_080_448, "混元 Hy-MT2 1.8B 翻译专模型（约 1.1GB，首选，翻译质量最佳）"),
        new("Hy-MT2-7B-Q4_K_M.gguf",
            $"{HfMirror}/tencent/Hy-MT2-7B-GGUF/resolve/main/Hy-MT2-7B-Q4_K_M.gguf",
            4_624_648_896, "混元 Hy-MT2 7B 翻译专模型（约 4.6GB，质量旗舰，建议 GPU）"),
        new("qwen2.5-1.5b-instruct-q4_k_m.gguf",
            $"{HfMirror}/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            1_100_000_000, "Qwen2.5 1.5B 通用（约 1.1GB，备选）"),
        new("qwen2.5-3b-instruct-q4_k_m.gguf",
            $"{HfMirror}/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            2_200_000_000, "Qwen2.5 3B 通用（约 2.2GB，备选）"),
        new("qwen2.5-7b-instruct-q4_k_m.gguf",
            $"{HfMirror}/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf",
            4_700_000_000, "Qwen2.5 7B 通用（约 4.7GB，备选）"),
    };

    public static Recommendation Recommend(GpuInfo gpu, int ramGb)
    {
        // 翻译专用模型（Hy-MT2）在所有档位都优先于通用模型：更小、翻译质量更高
        if (gpu.IsNvidia && gpu.VramMb >= 6000)
            return new Recommendation(
                $"检测到 NVIDIA {gpu.Name}（{gpu.VramMb}MB 显存）——推荐 CUDA 版 llama.cpp + Hy-MT2 7B 翻译模型全显存推理",
                "bin-win-cuda", "cudart",
                Models[1]);
        if (gpu.IsNvidia && gpu.VramMb >= 3000)
            return new Recommendation(
                $"检测到 NVIDIA {gpu.Name}（{gpu.VramMb}MB 显存）——推荐 CUDA 版 llama.cpp + Hy-MT2 1.8B 翻译模型",
                "bin-win-cuda", "cudart",
                Models[0]);
        return new Recommendation(
            $"未检测到可用 NVIDIA 显卡（{gpu.Name}），{ramGb}GB 内存——推荐 CPU 版 llama.cpp + Hy-MT2 1.8B 翻译模型（-t 线程数按物理核的 70% 设）",
            "bin-win-cpu", "bin-win-cpu",
            Models[0]);
    }

    /// <summary>从 GitHub Release 列表 JSON 拉取：遍历最近 30 个 release，取第一个带 Windows zip 的
    /// （latest tag 可能只是空壳发布，实测 v0.4.0 无任何资产——不能只看 latest）。解析抽成纯函数便于单测。</summary>
    public static async Task<(List<LlamaBuildInfo> Builds, string Error)> FetchLlamaBuilds(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30");
            req.Headers.UserAgent.ParseAdd("WoWTranslateControl/2.0");
            using var resp = await ProxyServer.SharedHttp.Client.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (ExtractBuilds(json), "");
        }
        catch (Exception ex)
        {
            return (new List<LlamaBuildInfo>(),
                $"无法访问 GitHub API（{ex.Message}）。可手动下载：https://github.com/ggml-org/llama.cpp/releases 最新版对应 zip，解压到 llama 目录。");
        }
    }

    /// <summary>解析 releases 列表 JSON，取第一个含 Windows zip 的 release。纯函数，可单测。</summary>
    public static List<LlamaBuildInfo> ExtractBuilds(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.ValueKind != JsonValueKind.Object ||
                !rel.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                continue;

            var builds = new List<LlamaBuildInfo>();
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains("win", StringComparison.OrdinalIgnoreCase)) continue; // 排除 linux/macos
                var label = name.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "CUDA（NVIDIA GPU 加速）"
                    : name.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ? "Vulkan"
                    : name.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU（AVX2 通用）"
                    : null;
                if (label == null) continue;
                if (name.Contains("cudart", StringComparison.OrdinalIgnoreCase))
                    label = "CUDA 运行库（cudart，CUDA 版必装）";
                builds.Add(new LlamaBuildInfo(name, url, label));
            }
            if (builds.Count > 0) return builds;
        }
        return new List<LlamaBuildInfo>();
    }

    /// <summary>按推荐序排列构建清单：推荐的放最前；同优先级 x64 优先于 arm64；cudart 运行库殿后。</summary>
    public static List<LlamaBuildInfo> OrderBuilds(IEnumerable<LlamaBuildInfo> builds, Recommendation rec)
    {
        return builds
            .OrderByDescending(b => b.Url.Contains(rec.LlamaAssetContains, StringComparison.OrdinalIgnoreCase) &&
                                    !b.AssetName.Contains("cudart", StringComparison.OrdinalIgnoreCase))
            .ThenBy(b => b.AssetName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(b => b.AssetName.Contains("cudart", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(b => b.AssetName, StringComparer.Ordinal)
            .ToList();
    }
}
