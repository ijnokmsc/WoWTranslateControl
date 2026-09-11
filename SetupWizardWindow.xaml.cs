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

    public SetupWizardWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
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
            if (builds.Count == 0)
            {
                TxtBuildNote.Text = string.IsNullOrEmpty(error)
                    ? "GitHub 最近 30 个 release 中未找到 Windows 构建（发布方式可能又变了），请到 llama.cpp Releases 页手动下载。"
                    : error;
                return;
            }
            _builds = ModelAdvisor.OrderBuilds(builds, _recommendation!);
            CmbBuild.ItemsSource = _builds;
            // 默认选中推荐的第一个
            CmbBuild.SelectedIndex = 0;
            TxtBuildNote.Text = $"共 {_builds.Count} 个可用构建。推荐 {(_recommendation!.LlamaAssetContains.Contains("cuda") ? "CUDA" : "CPU")} 版" +
                                "；CUDA 版还需下载 cudart 运行库一并解压。";
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
        await RunDownload(build.Url, dest, async () =>
        {
            Log($"已下载 {build.AssetName}。请在 {LlamaDir} 解压覆盖（CUDA 版同解 cudart 包）。");
            TxtStatus.Text = "llama.cpp 构建下载完成，请手动解压到 llama 目录";
        }).ConfigureAwait(false);
    }

    private async void BtnDownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (CmbModel.SelectedItem is not ModelInfo model) return;
        var dest = Path.Combine(LlamaDir, model.FileName);
        if (File.Exists(dest))
        {
            Log($"模型已存在：{dest}，跳过下载");
            _downloadedModelFile = model.FileName;
            BtnApplyModel.IsEnabled = true;
            TxtStatus.Text = "模型已就绪，可点「应用此模型」写入配置";
            return;
        }
        await RunDownload(model.Url, dest, async () =>
        {
            _downloadedModelFile = model.FileName;
            BtnApplyModel.IsEnabled = true;
            TxtStatus.Text = "模型下载完成，可点「应用此模型」写入配置";
            Log("模型下载完成");
        }).ConfigureAwait(false);
    }

    private async Task RunDownload(string url, string dest, Func<Task> onDone)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
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
            await Downloader.DownloadAsync(url, dest, progress, _cts.Token).ConfigureAwait(false);
            await onDone().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("下载已取消");
            TxtStatus.Text = "下载已取消";
        }
        catch (Exception ex)
        {
            Log($"下载失败：{ex.Message}");
            TxtStatus.Text = "下载失败（详见日志）";
        }
        finally
        {
            SetBusy(false);
            Progress.Value = 0;
        }
    }

    private void SetBusy(bool busy)
    {
        BtnDownloadBuild.IsEnabled = !busy;
        BtnDownloadModel.IsEnabled = !busy;
        BtnRefreshBuilds.IsEnabled = !busy;
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
