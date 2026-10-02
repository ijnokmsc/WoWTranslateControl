using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WoWTranslateControl;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;

    /// <summary>
    /// 调试模式：仅由启动参数 -Debug 开启。开启后全部日志生效
    /// （DLL 日志 / proxy_traffic.log / llama_mem.csv），默认一概不写。
    /// </summary>
    public static bool DebugMode { get; private set; }

    public App()
    {
        // 全局异常兜底：写 crash.log 后再弹窗，杜绝"运行无反应"式静默崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DebugMode = e.Args.Any(a =>
            a.Equals("-Debug", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--Debug", StringComparison.OrdinalIgnoreCase));

        // 启动自检日志（最早时机）：分发版用户环境各异，崩溃时凭此定位
        // 卡在运行时加载还是托管初始化（80131506 类问题唯一可观测点）
        try
        {
            var log = Path.Combine(AppContext.BaseDirectory, "startup.log");
            File.WriteAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] startup\n" +
                $"  .NET: {Environment.Version}\n" +
                $"  OS: {Environment.OSVersion.VersionString}\n" +
                $"  exe: {Environment.ProcessPath}\n" +
                $"  64bit: {Environment.Is64BitProcess}\n" +
                $"  debug: {DebugMode}\n");
        }
        catch { }

        // 单实例互斥体：双开会导致 8080/8081 端口占用冲突，第二个实例直接退出
        _singleInstanceMutex = new Mutex(true, "Local\\WoWTranslateControl.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WoWTranslate 控制台已在运行中（可能最小化在系统托盘）。\n" +
                            "请从托盘恢复使用，勿重复启动——双开会造成端口占用冲突。",
                "WoWTranslate 控制台", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(0);
            return;
        }
        base.OnStartup(e);
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss}] mutex acquired, base dir ready\n");
        }
        catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show($"发生未处理异常，程序将退出。\n\n{e.Exception.Message}\n\n" +
                        $"详情已写入程序目录 crash.log", "WoWTranslate 控制台",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }
}
