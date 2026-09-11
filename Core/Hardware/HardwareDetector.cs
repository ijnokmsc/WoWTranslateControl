using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WoWTranslateControl.Core.Hardware;

public sealed record GpuInfo(string Name, int VramMb, bool IsNvidia);

/// <summary>
/// 轻量硬件探测：优先 nvidia-smi（可拿到准确显存），失败回退注册表 Display 类设备。
/// 不引入 System.Management 依赖（避免离线 NuGet 还原风险）。
/// </summary>
public static class HardwareDetector
{
    public static GpuInfo GetGpu()
    {
        var (name, vram) = TryNvidiaSmi();
        if (name != null) return new GpuInfo(name, vram ?? 0, true);

        name = TryRegistryGpu();
        return new GpuInfo(name ?? "未知显卡", 0,
            name != null && name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name != null && (name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("GTX", StringComparison.OrdinalIgnoreCase)));
    }

    private static (string? Name, int? VramMb) TryNvidiaSmi()
    {
        try
        {
            foreach (var exe in new[] { "nvidia-smi",
                         @"C:\Windows\System32\nvidia-smi.exe",
                         @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
            {
                var psi = new ProcessStartInfo(exe,
                    "--query-gpu=name,memory.total --format=csv,noheader,nounits")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var line = output.Split('\n').FirstOrDefault(l => l.Contains(','));
                if (line == null) continue;
                var parts = line.Split(',');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[1].Trim(), out var mb))
                    return (parts[0].Trim(), mb);
            }
        }
        catch { }
        return (null, null);
    }

    private static string? TryRegistryGpu()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root == null) return null;
            foreach (var sub in root.GetSubKeyNames().Where(k => k.Length == 4 && k[0] == '0')
                         .OrderBy(k => k))
            {
                using var key = root.OpenSubKey(sub);
                var desc = key?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc)) return desc;
            }
        }
        catch { }
        return null;
    }

    public static int GetRamGb()
    {
        try
        {
            var psi = new ProcessStartInfo("wmic", "ComputerSystem get TotalPhysicalMemory")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var first = output.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));
            if (first != null && long.TryParse(first, out var bytes))
                return (int)(bytes / (1024L * 1024 * 1024));
        }
        catch { }
        return 0;
    }

    public static int GetLogicalCores() => Environment.ProcessorCount;
}
