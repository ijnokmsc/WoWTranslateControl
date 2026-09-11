using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WoWTranslateControl;

public partial class App : System.Windows.Application
{
    public App()
    {
        // 全局异常兜底：写 crash.log 后再弹窗，杜绝"运行无反应"式静默崩溃
        DispatcherUnhandledException += OnDispatcherUnhandledException;
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
