using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WoWTranslateControl.Core.Hardware;

/// <summary>
/// 大文件下载器：流式写盘 + 进度回调 + 取消。不整文件进内存。
/// GitHub/hf-mirror 均返回 Content-Length，可直接算百分比。
/// </summary>
public static class Downloader
{
    public static async Task DownloadAsync(string url, string destFile,
        IProgress<(long received, long total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("WoWTranslateControl/2.0");
        using var resp = await ProxyServer.SharedHttp.Client.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destFile + ".part", FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                progress.Report((received, total));
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report((received, total > 0 ? total : received));
        await dst.FlushAsync(ct).ConfigureAwait(false);
        dst.Dispose();

        File.Move(destFile + ".part", destFile, overwrite: true);
    }
}
