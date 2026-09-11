global using Color = System.Windows.Media.Color;
global using Application = System.Windows.Application;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Models;
using WoWTranslateControl;

namespace WoWTranslateControlSmoke;

/// <summary>
/// WoWTranslateControl 离屏 UI 冒烟：主窗口 / 术语表窗口 / 下载向导 三窗截图 + 控件断言。
/// 断言点：
///   1. 主窗口新增卡片（频道过滤 9 开关、翻译服务 ComboBox、插件一键配置、下载向导按钮）存在且可见；
///   2. 术语表窗口深色主题（Background=BgBrush）、按钮实际宽度足够容纳文字；
///   3. 向导窗口渲染出硬件摘要文本。
/// </summary>
internal static class Program
{
    private static int _ok;
    private static string _outDir = "";
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0]
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shots");
        Directory.CreateDirectory(outDir);
        _outDir = outDir;

        // 防止主窗口 Loaded 自动拉起 llama/代理：在冒烟工具目录放一份关闭自启的配置
        var cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        File.WriteAllText(cfgPath,
            """{"AutoStartLlama":false,"ProxyAutoStart":false,"MinimizeToTray":false,"LlamaDir":"D:\\llama.cpp","ModelFile":"none.gguf"}""");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var rd = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WoWTranslateControl;component/theme.xaml",
                    UriKind.Absolute)
            };
            app.Resources.MergedDictionaries.Add(rd);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[警告] 主程序资源加载失败：" + ex.Message);
        }

        Shot(app, outDir, "01_main", () =>
        {
            var w = new MainWindow
            {
                ShowInTaskbar = false,
                Left = -4000, Top = -4000,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        }, AssertMain, "主窗口：频道过滤/翻译服务/插件配置/向导入口");

        Shot(app, outDir, "02_glossary", () =>
        {
            var storePath = Path.Combine(Path.GetTempPath(),
                "wtc-smoke-" + Guid.NewGuid().ToString("N"), "glossary.json");
            var store = GlossaryStore.Load(storePath);
            store.Add("Ragnaros", "拉格纳罗斯");
            var w = new GlossaryWindow(store)
            {
                ShowInTaskbar = false,
                Left = -4000, Top = -4000,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        }, AssertGlossary, "术语表：深色统一 + 按钮文字完整");

        Shot(app, outDir, "03_wizard", () =>
        {
            var w = new SetupWizardWindow(new AppConfig())
            {
                ShowInTaskbar = false,
                Left = -4000, Top = -4000,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        }, AssertWizard, "下载向导：硬件摘要渲染");

        Console.WriteLine(Failures.Count == 0
            ? $"全部通过 ✓（{_ok} 项断言）"
            : $"失败 {Failures.Count} 项：\n  " + string.Join("\n  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    // ==================== 断言 ====================

    private static string? AssertMain(Window w)
    {
        var errors = new List<string>();
        Expect(w, errors,
            "ChkChanEnabled", "ChkChanSay", "ChkChanYell", "ChkChanWhisper",
            "ChkChanParty", "ChkChanGuild", "ChkChanRaid", "ChkChanBg",
            "ChkChanWorld", "ChkChanUntagged");
        Expect(w, errors, "TxtGameDir", "BtnConfigurePlugin", "BtnWizard", "TxtPluginReport");
        // DLL 轨道切换卡（ADR-007）+ 托盘开关
        Expect(w, errors, "CmbDllTrack", "BtnSwitchDll", "BtnRefreshDll", "TxtDllStatus", "ChkTray");
        var dllStatus = FindByName<System.Windows.Controls.TextBlock>(w, "TxtDllStatus");
        if (dllStatus == null || string.IsNullOrEmpty(dllStatus.Text) ||
            dllStatus.Text.Contains("检测失败"))
            errors.Add($"DLL 轨道状态未渲染（\"{dllStatus?.Text}\"）");
        Expect(w, errors, "ChkCache", "BtnGlossary"); // 旧控件不能因重构丢失
        // 系统提示词编辑器 + 恢复默认按钮
        Expect(w, errors, "TxtSystemPrompt", "BtnResetPrompt");
        var sp = FindByName<System.Windows.Controls.TextBox>(w, "TxtSystemPrompt");
        if (sp == null || sp.Text.Length < 20)
            errors.Add("系统提示词编辑框未加载默认内容");

        // 🔴 回归防线：TextBox 模板不得有任何改 Background 的触发器（系统 Aero2 悬停浅蓝填充回归检测）
        foreach (var tbx in FindDescendants<System.Windows.Controls.TextBox>(w))
        {
            var bgTrigger = tbx.Template.Triggers
                .OfType<System.Windows.Trigger>()
                .Any(t => t.Setters.OfType<Setter>()
                    .Any(st => st.Property == System.Windows.Controls.TextBox.BackgroundProperty));
            if (bgTrigger) errors.Add($"{tbx.Name}: TextBox 模板存在改背景的触发器（悬停浅蓝填充回归）");
            else _ok++;
        }

        // 🔴 回归防线：ToolTip 必须是深色自定义模板（系统默认浅色气泡 + 隐式浅色文字 → 提示看不清）
        var ttStyle = (System.Windows.Style?)System.Windows.Application.Current.Resources[typeof(System.Windows.Controls.ToolTip)]
                      ?? (System.Windows.Style?)w.Resources[typeof(System.Windows.Controls.ToolTip)];
        var ttHasTemplate = ttStyle?.Setters.OfType<System.Windows.Setter>()
            .Any(s => s.Property == System.Windows.Controls.ToolTip.TemplateProperty) == true;
        if (!ttHasTemplate) errors.Add("ToolTip 缺深色自定义模板（系统浅色气泡导致悬停提示看不清）");
        else _ok++;

        // 🔴 回归防线：ComboBox 内部 ToggleButton 必须是自定义空模板。
        // 系统 Aero2 模板悬停时画浅色高亮渐变，盖在深底上导致浅色文字不可见。
        foreach (var combo in FindDescendants<System.Windows.Controls.ComboBox>(w))
        {
            var toggle = FindDescendant<System.Windows.Controls.Primitives.ToggleButton>(combo);
            if (toggle == null) { errors.Add($"{combo.Name}: 模板缺 ToggleButton"); continue; }
            if (toggle.Template.Triggers.Count > 0)
                errors.Add($"{combo.Name}: ToggleButton 仍是系统模板（悬停浅色高亮会盖掉文字）");
            else _ok++;
        }

        // OpenAI 字段按 Provider 模式显隐：local 下应隐藏，切到 openai 应可见
        var cmb = FindByName<System.Windows.Controls.ComboBox>(w, "CmbProvider")
                  ?? throw new InvalidOperationException("缺少 CmbProvider");
        var ep = FindByName<System.Windows.Controls.TextBox>(w, "TxtOpenAiEndpoint")
                 ?? throw new InvalidOperationException("缺少 TxtOpenAiEndpoint");
        if (ep.Visibility != Visibility.Collapsed)
            errors.Add("local 模式下 OpenAI 端点框应隐藏");
        foreach (System.Windows.Controls.ComboBoxItem item in cmb.Items)
        {
            if ((string)item.Tag != "openai") continue;
            cmb.SelectedItem = item;
            break;
        }
        Pump();
        if (ep.Visibility != Visibility.Visible)
            errors.Add("切到 openai 模式后 OpenAI 端点框应可见");
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private static string? AssertGlossary(Window w)
    {
        var errors = new List<string>();
        if (((SolidColorBrush)w.Background)?.Color != Color.FromRgb(0x17, 0x17, 0x1D))
            errors.Add($"背景不是深色主题（{(w.Background is SolidColorBrush b ? b.Color.ToString() : "非纯色")}）");

        foreach (var name in new[] { "BtnAdd", "BtnUpdate", "BtnDelete", "BtnClear", "BtnImport", "BtnExport" })
        {
            var btn = FindByName<System.Windows.Controls.Button>(w, name);
            if (btn == null) { errors.Add($"缺少按钮 {name}"); continue; }
            // 渲染级截断判定：按钮内文字实际渲染宽不得超过 ContentPresenter 可用宽
            var presenter = FindDescendant<System.Windows.Controls.ContentPresenter>(btn);
            var tb = FindDescendant<System.Windows.Controls.TextBlock>(btn);
            if (presenter == null || tb == null) { errors.Add($"按钮 {name} 模板结构异常"); continue; }
            if (tb.ActualWidth > presenter.ActualWidth + 0.5)
                errors.Add($"按钮 {name} 文字截断（文字 {tb.ActualWidth:F0} > 可用 {presenter.ActualWidth:F0}）");
        }
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private static string? AssertWizard(Window w)
    {
        var errors = new List<string>();
        Expect(w, errors, "CmbBuild", "CmbModel", "BtnDownloadBuild", "BtnDownloadModel",
            "BtnApplyModel", "TxtHardware", "TxtAdvice", "Progress");
        var hw = FindByName<System.Windows.Controls.TextBlock>(w, "TxtHardware");
        // 硬件检测在后台线程跑（nvidia-smi/子进程），轮询等待完成
        for (var i = 0; i < 40 && (hw?.Text.Contains("GPU") != true); i++)
        {
            Thread.Sleep(100);
            Pump();
        }
        if (hw?.Text.Contains("GPU") != true)
            errors.Add($"硬件摘要未渲染（\"{hw?.Text}\"）");

        // 端到端：CmbBuild 必须从 GitHub 拉到真实构建清单（latest 空壳 release 回归验证）
        var cmbBuild = FindByName<System.Windows.Controls.ComboBox>(w, "CmbBuild");
        for (var i = 0; i < 150 && (cmbBuild?.Items.Count ?? 0) == 0; i++)
        {
            Thread.Sleep(100);
            Pump();
        }
        if (cmbBuild == null || cmbBuild.Items.Count == 0)
        {
            var note = FindByName<System.Windows.Controls.TextBlock>(w, "TxtBuildNote");
            errors.Add($"llama.cpp 构建列表为空（TxtBuildNote: \"{note?.Text}\"）");
        }
        else
        {
            _ok++;
            Console.WriteLine($"[OK] CmbBuild 拉到 {cmbBuild.Items.Count} 个构建，首项 {cmbBuild.Items[0]}");

            // 下拉展开态：条目必须命中自定义深色模板 + 悬停触发器存在
            // 注意：先选中再展开——展开后设置 IsSelected 会立即关闭弹层
            if (cmbBuild.ItemContainerGenerator.ContainerFromIndex(1) is System.Windows.Controls.ComboBoxItem it2)
                it2.IsSelected = true;
            cmbBuild.IsDropDownOpen = true;
            Pump(); Thread.Sleep(150); Pump();
            var c0 = cmbBuild.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ComboBoxItem;
            if (c0 == null || c0.Template.FindName("bd", c0) is not System.Windows.Controls.Border)
                errors.Add("ComboBoxItem 未命中自定义模板（悬停将出现系统浅色高亮、文字不可见）");
            else
            {
                var hoverTrig = c0.Template.Triggers.OfType<System.Windows.Trigger>()
                    .Any(t => t.Property == System.Windows.Controls.ComboBoxItem.IsMouseOverProperty);
                if (!hoverTrig) errors.Add("ComboBoxItem 模板缺悬停触发器");
                else _ok++;
            }
            // 渲染弹层截图（弹层是独立 hwnd，须直接渲染其 Child）
            var popup = FindDescendant<System.Windows.Controls.Primitives.Popup>(cmbBuild);
            if (popup?.Child is System.Windows.FrameworkElement pc && popup.IsOpen)
            {
                pc.UpdateLayout();
                var pw = (int)Math.Ceiling(pc.ActualWidth);
                var ph = (int)Math.Ceiling(Math.Min(pc.ActualHeight, 320));
                if (pw > 0 && ph > 0)
                {
                    var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(pc);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(_outDir, "04_dropdown.png"));
                    enc.Save(fs);
                    _ok++;
                }
            }
            cmbBuild.IsDropDownOpen = false;
            // 模型清单必须含 Hy-MT2 翻译专模型
            var cmbModel = FindByName<System.Windows.Controls.ComboBox>(w, "CmbModel");
            var hasHy = cmbModel!.Items.OfType<object>()
                .Any(m => m.ToString()!.Contains("Hy-MT2"));
            if (!hasHy) errors.Add("模型清单缺少 Hy-MT2 翻译专模型");
        }
        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    private static void Expect(Window w, List<string> errors, params string[] names)
    {
        foreach (var n in names)
        {
            var el = FindByName<FrameworkElement>(w, n);
            if (el == null) errors.Add($"缺少控件 {n}");
            else if (el.Visibility != Visibility.Visible) errors.Add($"控件 {n} 不可见");
            else _ok++;
        }
    }

    // ==================== 骨架 ====================

    private static void Shot(Application app, string outDir, string tag,
        Func<Window> create, Func<Window, string?> assert, string desc)
    {
        Window? w = null;
        try
        {
            w = create();
            w.UpdateLayout();
            Pump(); Pump();

            // 先断言（含异步数据轮询），再截图——保证截图带最终渲染数据
            var error = assert(w);

            var path = Path.Combine(outDir, tag + ".png");
            SaveWindowShot(w, path);

            // 左栏滚动区底部的 DLL 轨道切换卡单独渲染（主截图视口截不到）
            var dllCard = FindByName<System.Windows.FrameworkElement>(w, "CmbDllTrack");
            if (dllCard != null)
            {
                var parent = System.Windows.Media.VisualTreeHelper.GetParent(dllCard);
                while (parent is System.Windows.FrameworkElement fe &&
                       !(fe.Parent is System.Windows.Controls.Border))
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(fe);
                if (parent is System.Windows.FrameworkElement card)
                {
                    card.UpdateLayout();
                    var cw = (int)Math.Ceiling(card.ActualWidth);
                    var ch = (int)Math.Ceiling(card.ActualHeight);
                    if (cw > 0 && ch > 0)
                    {
                        var rtb = new RenderTargetBitmap(cw, ch, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(card);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using var fs = File.Create(Path.Combine(outDir, tag + "_dllcard.png"));
                        enc.Save(fs);
                    }
                }
            }

            DumpScrollInfo(w);

            if (error == null)
            {
                _ok++;
                Console.WriteLine($"[OK] {desc} → {path}");
            }
            else
            {
                Failures.Add($"[{FAIL_TAG}] {desc}：{error}");
                Console.WriteLine($"[{FAIL_TAG}] {desc}：{error}（截图 {path}）");
            }
        }
        catch (Exception ex)
        {
            Failures.Add($"[{FAIL_TAG}] {desc}：异常 {ex.Message}");
            Console.WriteLine($"[{FAIL_TAG}] {desc}：异常 {ex}");
        }
        finally
        {
            try { w?.Close(); } catch { }
        }
    }

    private const string FAIL_TAG = "FAIL";

    private static void Pump()
    {
        for (var i = 0; i < 2; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void SaveWindowShot(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            width = (int)window.Width;
            height = (int)window.Height;
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void DumpScrollInfo(Window window)
    {
        var found = new List<string>();
        Walk(window, found);
        foreach (var f in found) Console.WriteLine("      [滚动诊断] " + f);

        static void Walk(DependencyObject node, List<string> list)
        {
            var n = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is System.Windows.Controls.ScrollViewer sv)
                    list.Add($"ScrollViewer h={sv.ActualHeight:F0} viewport={sv.ViewportHeight:F0} " +
                             $"extent={sv.ExtentHeight:F0} scrollable={sv.ScrollableHeight:F0} " +
                             $"vbar={sv.ComputedVerticalScrollBarVisibility}");
                Walk(child, list);
            }
        }
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            var found = FindByName<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return FindDescendant<T>(child) ?? typed;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var sub in FindDescendants<T>(child))
                yield return sub;
        }
    }
}
