using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WoWTranslateControl.Core.Hardware;
using WoWTranslateControl.Models;

namespace WoWTranslateControl;

public partial class SetupWizardWindow : Window
{
    private readonly AppConfig _cfg;
    private ModelAdvisor.Recommendation? _recommendation;
    private System.Collections.Generic.List<LlamaBuildInfo>? _builds;
    private CancellationTokenSource? _cts;
    private string? _downloadedModelFile;
    private bool _busy;

    public SetupWizardWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        // 关窗即停：下载中关闭窗口会取消下载并清理 .tmp 残留，
        // 否则后台任务会一直占用文件，且新开的向导无法控制旧下载
        Closing += (_, _) =>
        {
            if (_busy)
            {
                _cts?.Cancel();
                Log("窗口已关闭：下载已停止，临时文件已清理");
            }
        };
        Loaded += async (_, _) => await InitializeAsync().ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        // WMI/子进程探测是阻塞调用，丢线程池避免卡 UI
        var (gpu, ram) = await Task.Run(() =>
            (HardwareDetector.GetGpu(), HardwareDetector.GetRamGb())).ConfigureAwait(false);

        var rec = ModelAdvisor.Recommend(gpu, ram);
        _recommendation = rec;

        await Dispatcher.InvokeAsync(() =>
        {
            TxtHardware.Text = $"GPU：{gpu.Name}（显存 {(gpu.VramMb > 0 ? gpu.VramMb + "MB" : "未知")}）    " +
                               $"内存：{ram}GB    逻辑核：{HardwareDetector.GetLogicalCores()}";
            TxtAdvice.Text = rec.Summary;
            Log($"推荐：{rec.LlamaAssetContains} 构建 + {rec.Model.FileName}");

            CmbModel.ItemsSource = ModelAdvisor.Models;
            CmbModel.SelectedItem = rec.Model;
            TxtStatus.Text = "检测完成";
        });

        await RefreshBuildsAsync().ConfigureAwait(false);
    }

    private async Task RefreshBuildsAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            BtnRefreshBuilds.IsEnabled = false;
            TxtBuildNote.Text = "正在获取 llama.cpp 最新 Release 资产清单…";
        });
        var (builds, error) = await ModelAdvisor.FetchLlamaBuilds(_cts?.Token ?? CancellationToken.None)
            .ConfigureAwait(false);

        await Dispatcher.InvokeAsync(() =>
        {
            BtnRefreshBuilds.IsEnabled = true;
            // 手动部署支持：已放好 llama-server.exe 的用户无需再下载构建
            if (File.Exists(Path.Combine(LlamaDir, "llama-server.exe")))
            {
                TxtBuildNote.Text = $"✔ 检测到 {LlamaDir}\\llama-server.exe 已存在——llama.cpp 已就绪，无需重复下载；如需更换版本再选下方构建下载覆盖。";
                if (builds.Count > 0)
                {
                    _builds = ModelAdvisor.OrderBuilds(builds, _recommendation!);
                    CmbBuild.ItemsSource = _builds;
                    CmbBuild.SelectedIndex = 0;
                ReportLlamaDirUsage();
                }
                return;
            }
            if (builds.Count == 0)
            {
                TxtBuildNote.Text = string.IsNullOrEmpty(error)
                    ? "GitHub 最近 30 个 release 中未找到 Windows 构建（发布方式可能又变了），请到 llama.cpp Releases 页手动下载 zip，解压到 llama 目录（保留 llama-server.exe）。"
                    : error;
                return;
            }
            _builds = ModelAdvisor.OrderBuilds(builds, _recommendation!);
            CmbBuild.ItemsSource = _builds;
            // 默认选中推荐的第一个
            CmbBuild.SelectedIndex = 0;
            TxtBuildNote.Text = $"共 {_builds.Count} 个可用构建。推荐 {(_recommendation!.LlamaAssetContains.Contains("cuda") ? "CUDA" : "CPU")} 版" +
                                "；CUDA 版还需下载 cudart 运行库一并解压。下载慢也可手动下载 zip 放入 llama 目录，重新点「刷新」即可识别。";
        });
    }

    private void Log(string msg)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        TxtLog.ScrollToEnd();
    }

    private async void BtnRefreshBuilds_Click(object sender, RoutedEventArgs e)
        => await RefreshBuildsAsync().ConfigureAwait(false);

    private string LlamaDir => _cfg.LlamaDir;

    private async void BtnDownloadBuild_Click(object sender, RoutedEventArgs e)
    {
        if (CmbBuild.SelectedItem is not LlamaBuildInfo build) return;
        var dest = Path.Combine(LlamaDir, build.AssetName);

        // 手动下载支持：zip 已放入 llama 目录 → 跳过下载，直接解压
        if (File.Exists(dest))
        {
            Log($"检测到 {build.AssetName} 已存在，跳过下载，直接解压…");
            ExtractBuildZip(dest);
            return;
        }
        await RunDownload(build.Url, dest, () => ExtractBuildZip(dest)).ConfigureAwait(false);
    }

    /// <summary>解压 llama.cpp / cudart 构建包到 llama 目录（阻塞几秒，UI 线程可接受）。</summary>
    private void ExtractBuildZip(string zipPath)
    {
        Log($"正在解压 {Path.GetFileName(zipPath)} 到 {LlamaDir} …");
        try
        {
            // 后台线程解压（数秒），完成后回 UI 报告
            Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(
                zipPath, LlamaDir, overwriteFiles: true)).GetAwaiter().GetResult();
            TxtStatus.Text = "llama.cpp 构建已解压完成";
            Log($"解压完成：{LlamaDir}\\llama-server.exe 应已就绪，返回主窗口即可启动 llama-server");
            try { System.IO.File.Delete(zipPath); } catch { }
            TxtStatus.Text = "llama.cpp 构建已解压完成（安装包已删除以释放空间）";
            ReportLlamaDirUsage();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "自动解压失败，请手动解压";
            Log($"自动解压失败：{ex.Message}。请手动将 {zipPath} 解压到 {LlamaDir}（需保留 llama-server.exe）");
        }
    }

    private async void BtnDownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (CmbModel.SelectedItem is not ModelInfo model) return;
        var dest = Path.Combine(LlamaDir, model.FileName);
        if (File.Exists(dest))
        {
            Log($"模型已存在：{dest}，跳过下载（也支持手动下载 gguf 放到此路径）");
            _downloadedModelFile = model.FileName;
            BtnApplyModel.IsEnabled = true;
            TxtStatus.Text = "模型已就绪，可点「应用此模型」写入配置";
            return;
        }
        await RunDownload(model.Url, dest, () =>
        {
            _downloadedModelFile = model.FileName;
            BtnApplyModel.IsEnabled = true;
            TxtStatus.Text = "模型下载完成，可点「应用此模型」写入配置";
            Log("模型下载完成");
            ReportLlamaDirUsage();
        }).ConfigureAwait(false);
    }

    private async Task RunDownload(string url, string dest, Action onDone)
    {
        SetBusy(true);
        try { Directory.CreateDirectory(LlamaDir); } catch { }
        _cts = new CancellationTokenSource();
        // 先写 .tmp，完成后原子改名——半截文件永远是 .tmp，取消/失败时整只清理
        var tmp = dest + ".tmp";
        Progress<(long received, long total)> progress = new(t =>
        {
            var pct = t.total > 0 ? t.received * 100.0 / t.total : 0;
            Progress.Value = pct;
            TxtStatus.Text = t.total > 0
                ? $"{Path.GetFileName(dest)}  {t.received / 1048576.0:F0}/{t.total / 1048576.0:F0} MB（{pct:F1}%）"
                : $"{Path.GetFileName(dest)}  {t.received / 1048576.0:F0} MB";
        });
        try
        {
            Log($"开始下载：{url}");
            TxtStatus.Text = $"下载中：{Path.GetFileName(dest)}";
            // 不带 ConfigureAwait(false)：下载完成后回到 UI 线程，
            // 使 catch/finally 里的 UI 操作与 onDone 回调线程安全
            //（v25 崩溃根因：threadpool 续体上调 SetBusy/Progress 抛跨线程异常）
            await Downloader.DownloadAsync(url, tmp, progress, _cts.Token);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            onDone();
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(tmp);
            Log("下载已停止，未完成的临时文件已清理（原文件不受影响）");
            TxtStatus.Text = "下载已停止";
        }
        catch (Exception ex)
        {
            TryDeletePartial(tmp);
            Log($"下载失败：{ex.Message}（未完成的临时文件已清理）");
            Log($"可手动下载（地址见上方「开始下载」日志行），放到 {Path.GetDirectoryName(dest)} 后重试：" +
                "llama.cpp 的 zip 放入后在向导重新下载同文件会触发自动解压，或自行解压到 llama 目录；" +
                "模型 gguf 放入即视为就绪。");
            TxtStatus.Text = "下载失败（详见日志）";
        }
        finally
        {
            SetBusy(false);
            Progress.Value = 0;
        }
    }

    /// <summary>清理未完成的下载残留（.tmp 半截文件）。成品 dest 不动——保留上一次的好文件。</summary>
    private void TryDeletePartial(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;
        _cts.Cancel();
        Log("正在停止下载…");
        BtnCancelDownload.IsEnabled = false;
    }

    // ==================== llama 目录大文件检测 / 清理 ====================

    private sealed record LargeFile(string Name, long Size, string Kind, bool Reclaimable);

    /// <summary>扫描 llama 目录中 >50MB 的文件并分类：程序 / 使用中模型 / 冗余项。</summary>
    private System.Collections.Generic.List<LargeFile> ScanLlamaDir()
    {
        var list = new System.Collections.Generic.List<LargeFile>();
        try
        {
            var dir = new DirectoryInfo(LlamaDir);
            if (!dir.Exists) return list;
            foreach (var f in dir.GetFiles())
            {
                if (f.Length < 50L * 1024 * 1024) continue;
                string kind;
                var reclaimable = false;
                if (string.Equals(f.Name, "llama-server.exe", StringComparison.OrdinalIgnoreCase))
                    kind = "程序";
                else if (string.Equals(f.Name, _cfg.ModelFile, StringComparison.OrdinalIgnoreCase))
                    kind = "使用中模型";
                else if (f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                { kind = "未使用模型"; reclaimable = true; }
                else if (f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                { kind = "遗留安装包"; reclaimable = true; }
                else if (f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                { kind = "残留临时文件"; reclaimable = true; }
                else kind = "其他大文件";
                list.Add(new LargeFile(f.Name, f.Length, kind, reclaimable));
            }
        }
        catch { }
        return list;
    }

    private void ReportLlamaDirUsage()
    {
        var items = ScanLlamaDir();
        if (items.Count == 0) return;
        Log("llama 目录大文件检测（>50MB）：");
        foreach (var it in items)
            Log($"  [{it.Kind}] {it.Name}  {it.Size / 1048576.0:F0} MB" +
                (it.Reclaimable ? "  ← 冗余，可清理" : ""));
        var reclaim = items.Where(i => i.Reclaimable).Sum(i => i.Size);
        if (reclaim > 0)
            Log($"  共 {reclaim / 1048576.0:F0} MB 可释放：点「清理未使用大文件」按钮处理");
    }

    private void BtnCleanLlamaDir_Click(object sender, RoutedEventArgs e)
    {
        var targets = ScanLlamaDir().Where(i => i.Reclaimable).ToList();
        if (targets.Count == 0)
        {
            Log("llama 目录没有可清理的冗余大文件");
            TxtStatus.Text = "没有可清理的冗余大文件";
            return;
        }
        var bs = Convert.ToChar(92);
        var msg = "将删除以下文件（使用中的模型和 llama-server.exe 不会被动）:" + bs.ToString() + "n" + bs.ToString() + "n" +
                  string.Join(bs.ToString() + "n", targets.Select(t => $"[{t.Kind}] {t.Name}  {t.Size / 1048576.0:F0} MB")) +
                  bs + 'n' + bs + $"共可释放 {targets.Sum(t => t.Size) / 1048576.0:F0} MB。确认删除？";
        if (MessageBox.Show(msg, "清理确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var freed = 0L;
        var failed = 0;
        foreach (var t in targets)
        {
            try
            {
                System.IO.File.Delete(Path.Combine(LlamaDir, t.Name));
                freed += t.Size;
            }
            catch (Exception ex)
            {
                failed++;
                Log($"删除失败 [{t.Name}]：{ex.Message}（文件可能被占用）");
            }
        }
        Log($"清理完成：删除 {targets.Count - failed} 个文件，释放 {freed / 1048576.0:F0} MB" +
            (failed > 0 ? $"；{failed} 个删除失败（详见日志）" : ""));
        TxtStatus.Text = $"清理完成，释放 {freed / 1048576.0:F0} MB";
        ReportLlamaDirUsage();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnDownloadBuild.IsEnabled = !busy;
        BtnDownloadModel.IsEnabled = !busy;
        BtnRefreshBuilds.IsEnabled = !busy;
        BtnCancelDownload.IsEnabled = busy;
    }

    private void BtnApplyModel_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedModelFile == null) return;
        _cfg.LlamaDir = LlamaDir;
        _cfg.ModelFile = _downloadedModelFile;
        _cfg.Save();
        Log($"已写入配置：llama 目录 {LlamaDir}，模型 {_downloadedModelFile}。重启 llama-server 生效。");
        TxtStatus.Text = "配置已应用（重启 llama-server 生效）";
    }
}
