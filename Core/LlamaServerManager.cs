using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core;

public enum LlamaState { Stopped, Starting, Running, Crashed }

/// <summary>
/// 管理 llama-server.exe 的启动、停止、健康探测与输出采集。
///
/// 默认参数对齐现有 start_llama.bat：
///   -m Hy-MT2-1.8B-Q4_K_M.gguf -t 20 -c 2048 -ngl 0
///   --host 127.0.0.1 --port 8081 --no-webui --flash-attn off
///
/// 注意：不做任何并发限制或降线程处理。
/// -t 20 在 14 物理核 / 28 逻辑核的 Xeon E5-2680 v4 上属合理配置，
/// CPU 满载的根因是无效流量（88.6% 为系统播报），由过滤层解决。
/// </summary>
public sealed class LlamaServerManager : IDisposable
{
    private readonly AppConfig _cfg;
    private Process? _proc;
    private readonly StringBuilder _outputTail = new();
    private readonly object _sync = new();
    private DateTime _startedAt;

    // 生命周期锁：Start/Stop/Restart 与 Exited 回调（线程池线程）互斥。
    // Monitor 可重入，Restart 内部再调 Stop/Start 不会自锁。
    private readonly object _lifeSync = new();

    // 主动停止标志：置位后 Exited 不再报 Crashed（Stop 等 Kill 完成后，
    // Exited 事件仍可能异步迟到，单靠时序判断会误报）。
    private volatile bool _stopping;

    // 内存超限自动重启（v41）
    private int _memOverSeconds;
    private DateTime _memCooldownUntil = DateTime.MinValue;
    private DateTime _lastAutoRestart = DateTime.MinValue;

    private const int MemOverSecondsToRestart = 60;     // 连续超限秒数
    private const int MemRestartCooldownMinutes = 10;   // 重启后冷却，防反复拉起
    private const int CrashAutoRestartGapMinutes = 5;   // 崩溃自动拉起最小间隔
    private const int CrashAutoRestartDelayMs = 5000;   // 崩溃后延迟拉起，给退出收尾留时间

    public LlamaState State { get; private set; } = LlamaState.Stopped;
    public int? Pid => _proc?.Id;
    public TimeSpan Uptime => State == LlamaState.Running || State == LlamaState.Starting
        ? DateTime.Now - _startedAt
        : TimeSpan.Zero;

    /// <summary>llama-server 工作集内存（MB）。未运行返回 null；调用方按秒采样即可。</summary>
    public double? WorkingSetMB
    {
        get
        {
            if (_proc is not { HasExited: false }) return null;
            try
            {
                _proc.Refresh();
                return _proc.WorkingSet64 / 1024.0 / 1024.0;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 清理上次会话残留的 llama-server 孤儿进程（控制台被强杀/崩溃时子进程存活）。
    /// 在主窗口构造（Mutex 已获持有）后调用——单实例保证此刻存在的 llama-server
    /// 均为孤儿。返回清理的进程数。
    /// </summary>
    public static int KillOrphans()
    {
        int killed = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch { /* 已退出/权限不足：跳过 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 枚举失败不影响启动 */ }
        return killed;
    }

    public event Action<LlamaState>? OnStateChanged;
    public event Action<string>? OnOutput;
    public event Action<string>? OnLog;

    public LlamaServerManager(AppConfig cfg) => _cfg = cfg;

    public string BuildArguments()
    {
        var model = Path.IsPathRooted(_cfg.ModelFile)
            ? _cfg.ModelFile
            : Path.Combine(_cfg.LlamaDir, _cfg.ModelFile);

        return $"-m \"{model}\" " +
               $"-t {_cfg.Threads} " +
               $"-c {_cfg.ContextSize} " +
               "-ngl 0 " +
               "--host 127.0.0.1 " +
               $"--port {_cfg.UpstreamPort} " +
               "--no-webui " +
               "--flash-attn off";
    }

    public bool Start()
    {
        lock (_lifeSync)
        {
            if (State == LlamaState.Running || State == LlamaState.Starting)
            {
                OnLog?.Invoke("llama-server 已在运行中");
                return true;
            }
            _stopping = false;

            var exe = Path.Combine(_cfg.LlamaDir, "llama-server.exe");
            if (!File.Exists(exe))
            {
                OnLog?.Invoke($"未找到可执行文件：{exe}");
                State = LlamaState.Crashed;
                OnStateChanged?.Invoke(State);
                return false;
            }

            var modelPath = Path.IsPathRooted(_cfg.ModelFile)
                ? _cfg.ModelFile
                : Path.Combine(_cfg.LlamaDir, _cfg.ModelFile);
            if (!File.Exists(modelPath))
            {
                OnLog?.Invoke($"未找到模型文件：{modelPath}");
                State = LlamaState.Crashed;
                OnStateChanged?.Invoke(State);
                return false;
            }

            if (IsPortListening(_cfg.UpstreamPort))
            {
                OnLog?.Invoke($"端口 {_cfg.UpstreamPort} 已被占用，可能有其他实例在运行。" +
                              "将直接视为已就绪。");
                State = LlamaState.Running;
                _startedAt = DateTime.Now;
                OnStateChanged?.Invoke(State);
                return true;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = BuildArguments(),
                    WorkingDirectory = _cfg.LlamaDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (_, e) => Append(e.Data);
                _proc.ErrorDataReceived += (_, e) => Append(e.Data);
                _proc.Exited += (_, _) =>
                {
                    // 主动停止/重启期间迟到的退出事件不报 Crashed
                    if (_stopping || State == LlamaState.Stopped) return;
                    State = LlamaState.Crashed;
                    OnLog?.Invoke($"llama-server 进程已退出（ExitCode={SafeExitCode()}）");
                    OnStateChanged?.Invoke(State);
                    TryAutoRestartAfterCrash();
                };

                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                _startedAt = DateTime.Now;
                State = LlamaState.Starting;
                OnStateChanged?.Invoke(State);
                OnLog?.Invoke($"llama-server 启动中 (PID {_proc.Id})，参数：{BuildArguments()}");

                // 异步等待端口就绪，不阻塞 UI 线程
                _ = WaitForReadyAsync();
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"启动失败：{ex.Message}");
                State = LlamaState.Crashed;
                OnStateChanged?.Invoke(State);
                return false;
            }
        }
    }

    /// <summary>崩溃后自动拉起：受 LlamaAutoRestart 开关与最小间隔（防连崩循环）约束。</summary>
    private void TryAutoRestartAfterCrash()
    {
        if (!_cfg.LlamaAutoRestart) return;
        var now = DateTime.Now;
        lock (_lifeSync)
        {
            if (State != LlamaState.Crashed) return;
            if ((now - _lastAutoRestart).TotalMinutes < CrashAutoRestartGapMinutes)
            {
                OnLog?.Invoke($"距上次自动重启不足 {CrashAutoRestartGapMinutes} 分钟" +
                              "（疑似连续崩溃），已暂停自动拉起，请手动排查。");
                return;
            }
            _lastAutoRestart = now;
        }
        OnLog?.Invoke($"{CrashAutoRestartDelayMs / 1000} 秒后自动重启 llama-server…");
        Task.Delay(CrashAutoRestartDelayMs).ContinueWith(_ =>
        {
            lock (_lifeSync)
            {
                if (State == LlamaState.Crashed) Start();
            }
        });
    }

    /// <summary>重启 llama-server（先杀干净再拉起）。reason 写入事件日志。</summary>
    public void Restart(string reason)
    {
        lock (_lifeSync)
        {
            OnLog?.Invoke($"llama-server 重启：{reason}");
            Stop();
            Start();
        }
    }

    /// <summary>
    /// 每秒调用（UpdateClock 节拍）：Running 且工作集连续超阈值 60 秒 →
    /// 后台自动重启回收内存；重启后 10 分钟冷却期内不再触发。
    /// </summary>
    public void CheckMemoryRestart()
    {
        if (!_cfg.LlamaAutoRestart) return;
        if (State != LlamaState.Running) { _memOverSeconds = 0; return; }
        var mem = WorkingSetMB;
        if (!mem.HasValue || mem.Value <= _cfg.LlamaMemLimitMB)
        {
            _memOverSeconds = 0;
            return;
        }
        if (DateTime.Now < _memCooldownUntil) return;   // 冷却期：不累计不触发
        _memOverSeconds++;
        if (_memOverSeconds < MemOverSecondsToRestart) return;
        _memOverSeconds = 0;
        _memCooldownUntil = DateTime.Now.AddMinutes(MemRestartCooldownMinutes);
        var mb = mem.Value;
        var limit = _cfg.LlamaMemLimitMB;
        Task.Run(() => Restart(
            $"内存超限 {mb:N0} MB > 阈值 {limit} MB，持续 {MemOverSecondsToRestart} 秒"));
    }

    private int SafeExitCode()
    {
        try { return _proc?.ExitCode ?? -1; } catch { return -1; }
    }

    private async Task WaitForReadyAsync()
    {
        // 模型加载需要时间，最多等 120 秒
        for (var i = 0; i < 240; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (State == LlamaState.Stopped || State == LlamaState.Crashed) return;
            if (_proc is { HasExited: true }) return;

            if (IsPortListening(_cfg.UpstreamPort))
            {
                State = LlamaState.Running;
                OnLog?.Invoke($"llama-server 已就绪（端口 {_cfg.UpstreamPort} 可连接），" +
                              $"加载耗时约 {(DateTime.Now - _startedAt).TotalSeconds:F1} 秒");
                OnStateChanged?.Invoke(State);
                return;
            }
        }

        if (State == LlamaState.Starting)
        {
            OnLog?.Invoke("等待端口就绪超时（120 秒），请查看输出日志排查");
        }
    }

    public static bool IsPortListening(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(400) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        lock (_lifeSync)
        {
            // 先置主动停止标志：Kill 后 Exited 事件可能异步迟到，避免误报 Crashed
            _stopping = true;
            if (_proc is { HasExited: false })
        {
            try
            {
                // 先尝试优雅关闭，超时后强制结束
                _proc.CloseMainWindow();
                if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true);
                OnLog?.Invoke("llama-server 已停止");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"停止时出错：{ex.Message}");
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        else
        {
            OnLog?.Invoke("llama-server 未在运行");
        }

            _proc?.Dispose();
            _proc = null;
            State = LlamaState.Stopped;
            OnStateChanged?.Invoke(State);
        }
    }

    private void Append(string? line)
    {
        if (line == null) return;
        lock (_sync)
        {
            _outputTail.AppendLine(line);
            // 只保留最近 400 行，避免长时间运行内存增长
            if (_outputTail.Length > 200_000)
            {
                var s = _outputTail.ToString();
                _outputTail.Clear();
                _outputTail.Append(s[^100_000..]);
            }
        }
        OnOutput?.Invoke(line);
    }

    public string GetOutputTail(int lines = 200)
    {
        lock (_sync)
        {
            var all = _outputTail.ToString().Split('\n');
            return string.Join('\n', all.TakeLast(lines));
        }
    }

    public void ClearOutput()
    {
        lock (_sync) _outputTail.Clear();
    }

    public void Dispose()
    {
        _stopping = true;   // 退出期不触发 Crashed 事件与自动拉起
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }
        _proc?.Dispose();
    }
}
