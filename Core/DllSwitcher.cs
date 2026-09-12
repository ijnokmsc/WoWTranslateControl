using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WoWTranslateControl.Core;

/// <summary>
/// 插件 DLL 轨道切换（ADR-007 双轨并行的操作面）。
///
/// Track A（gs）     = 现役 WoWTranslate GS 插件链路：dinput8.dll + WoWTranslate335.dll + dlls.txt
/// Track B（direct） = 自有 Direct DLL（实验）：dinput8.dll + WoWTranslateDirect.dll + dlls.txt
///
/// 文件语义（游戏根目录）：
///   - 被切走的轨道整组文件存放在 wtc_backup/&lt;轨道&gt;/，首次 stash 时保留原始基线（不覆盖）；
///   - direct 轨道的 DLL 资产来源：控制台 assets/direct-dll/（或游戏目录 wtc_direct_dll/），
///     资产未就绪时 Track B 选项禁用；
///   - 切换 = 「stash 当前 → 恢复目标 → Probe 自检」，自检失败自动回滚。
///
/// 安全铁律与 PluginConfigurator 一致：Wow.exe 运行时拒绝执行（DLL 被占用 + 避免半切换状态）。
/// </summary>
public sealed class DllSwitcher
{
    public const string TrackGs = "gs";
    public const string TrackDirect = "direct";

    /// <summary>两组轨道涉及的文件名（相对游戏根目录）。</summary>
    private static readonly string[] TrackFiles =
    {
        "dinput8.dll", "WoWTranslate335.dll", "WoWTranslateDirect.dll", "dlls.txt",
    };

    public sealed record SwitchStatus(
        string Current,             // "gs" | "direct" | "disabled" | "unknown"
        string CurrentText,
        bool DirectAssetsReady,
        string DirectAssetsDir,
        List<string> Details);

    // ==================== 探测 ====================

    public static SwitchStatus Probe(string gameDir, string directAssetsDir)
    {
        var details = new List<string>();

        var hasDinput = File.Exists(Path.Combine(gameDir, "dinput8.dll"));
        var hasGs = File.Exists(Path.Combine(gameDir, "WoWTranslate335.dll"));
        var hasDirect = File.Exists(Path.Combine(gameDir, "WoWTranslateDirect.dll"));

        // 目录有效性提示（不改变轨道判定，但让配错目录一眼可见）
        if (!File.Exists(Path.Combine(gameDir, "Wow.exe")))
            details.Add($"⚠ 该目录没有 Wow.exe，可能不是客户端根目录：{gameDir}");

        var (ready, assetDir) = FindDirectAssets(gameDir, directAssetsDir);
        if (ready)
            details.Add($"Direct 资产就绪：{assetDir}");
        else
            details.Add($"Direct 资产未就绪（需在 {assetDir} 放置 dinput8.dll + WoWTranslateDirect.dll）");

        string cur, text;
        if (hasDinput && hasDirect) { cur = TrackDirect; text = "Track B — 自有 Direct DLL（实验）"; }
        else if (hasDinput && hasGs) { cur = TrackGs; text = "Track A — WoWTranslate GS（现役）"; }
        else if (!hasDinput) { cur = "disabled"; text = "未启用（游戏根目录无 dinput8.dll）"; }
        else { cur = "unknown"; text = "未知状态（dinput8.dll 存在但未发现任一轨道 DLL）"; }

        if (Directory.Exists(BackupDir(gameDir, TrackGs)))
            details.Add("GS 轨道备份存在：wtc_backup/gs");
        if (Directory.Exists(BackupDir(gameDir, TrackDirect)))
            details.Add("Direct 轨道备份存在：wtc_backup/direct");
        foreach (var f in TrackFiles.Where(f => File.Exists(Path.Combine(gameDir, f))))
            details.Add($"根目录文件：{f}");

        return new SwitchStatus(cur, text, ready, assetDir, details);
    }

    private static (bool Ready, string Dir) FindDirectAssets(string gameDir, string consoleAssetsDir)
    {
        // 优先控制台自带 assets/direct-dll，其次游戏目录内 wtc_direct_dll
        foreach (var dir in new[] { consoleAssetsDir, Path.Combine(gameDir, "wtc_direct_dll") })
        {
            if (File.Exists(Path.Combine(dir, "dinput8.dll")) &&
                File.Exists(Path.Combine(dir, "WoWTranslateDirect.dll")))
                return (true, dir);
        }
        return (false, consoleAssetsDir);
    }

    private static string BackupDir(string gameDir, string track) =>
        Path.Combine(gameDir, "wtc_backup", track);

    // ==================== Track B 配置（v16 全自治驱动） ====================

    /// <summary>
    /// 写游戏根目录 WoWTranslateDirect.json（Direct DLL v14 起启动时读取）。
    /// endpoint 指向控制台伪装 OpenAI 端点，displayMode/displayPrefix 由内嵌驱动 Lua 消费。
    /// UTF-8 无 BOM（DLL 侧 nlohmann::json 不认 BOM）。
    /// </summary>
    public static string WriteDirectConfig(string gameDir, int listenPort,
        string displayMode, string displayPrefix, string outgoingMode = "off")
    {
        if (displayMode != "replace" && displayMode != "both") displayMode = "replace";
        if (outgoingMode != "off" && outgoingMode != "replace" && outgoingMode != "both")
            outgoingMode = "off";
        var json =
            "{\n" +
            "  \"provider\": \"openai\",\n" +
            "  \"apiKey\": \"wtc-local\",\n" +
            "  \"model\": \"WoWTranslateControl\",\n" +
            $"  \"endpoint\": \"http://127.0.0.1:{listenPort}/v1/chat/completions\",\n" +
            $"  \"displayMode\": \"{displayMode}\",\n" +
            $"  \"displayPrefix\": \"{EscapeJson(displayPrefix ?? "[译]")}\",\n" +
            $"  \"outgoingMode\": \"{outgoingMode}\"\n" +
            "}\n";
        var path = Path.Combine(gameDir, "WoWTranslateDirect.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    // ==================== 切换 ====================

    public static List<string> SwitchTo(string gameDir, string target, string directAssetsDir)
        => SwitchTo(gameDir, target, directAssetsDir, wowRunning: null);

    /// <param name="wowRunning">游戏进程门禁（可注入；测试传 () => false 绕过）。</param>
    /// <param name="listenPort">控制台代理监听端口（Track B 部署时写进 WoWTranslateDirect.json）。</param>
    /// <param name="displayMode">Track B 驱动显示模式："replace" | "both"。</param>
    /// <param name="displayPrefix">Track B both 模式译文行前缀。</param>
    public static List<string> SwitchTo(string gameDir, string target, string directAssetsDir,
        Func<bool>? wowRunning = null,
        int listenPort = 8080,
        string displayMode = "replace",
        string displayPrefix = "[译]",
        string outgoingMode = "off")
    {
        var lines = new List<string>();
        try
        {
            if (target != TrackGs && target != TrackDirect)
            {
                lines.Add($"❌ 未知轨道：{target}");
                return lines;
            }

            if (wowRunning?.Invoke() ?? PluginConfigurator.IsWowRunning())
            {
                lines.Add("❌ 检测到 Wow.exe 正在运行。切换 DLL 需要游戏完全退出（文件被占用 + 避免半切换状态）。");
                return lines;
            }

            if (!Directory.Exists(gameDir))
            {
                lines.Add($"❌ 游戏目录不存在：{gameDir}");
                return lines;
            }

            if (!File.Exists(Path.Combine(gameDir, "Wow.exe")))
            {
                lines.Add($"❌ 目录中未找到 Wow.exe，拒绝切换：{gameDir}");
                lines.Add("   请在「游戏目录」框填入 3.3.5 客户端根目录（可点右侧「浏览…」手动选择）。");
                return lines;
            }

            var status = Probe(gameDir, directAssetsDir);
            lines.Add($"当前状态：{status.CurrentText}");

            if (status.Current == target)
            {
                lines.Add("✔ 已处于目标轨道，无需切换");
                return lines;
            }

            if (target == TrackDirect && !status.DirectAssetsReady)
            {
                lines.Add($"❌ Track B 资产未就绪：{status.DirectAssetsDir} 中缺少 dinput8.dll / WoWTranslateDirect.dll。");
                lines.Add("   构建 Direct DLL 后把整组文件放入该目录再切换（S6/T1 阶段产物）。");
                return lines;
            }

            // ---- 1. stash 当前轨道（只保留第一次的原始基线，避免坏状态覆盖好备份）----
            if (status.Current is TrackGs or TrackDirect)
            {
                var stashDir = BackupDir(gameDir, status.Current);
                Directory.CreateDirectory(stashDir);
                foreach (var f in TrackFiles)
                {
                    var src = Path.Combine(gameDir, f);
                    var dst = Path.Combine(stashDir, f);
                    if (File.Exists(src) && !File.Exists(dst))
                    {
                        File.Copy(src, dst, overwrite: false);
                        lines.Add($"  已备份 {f} → wtc_backup/{status.Current}/{f}");
                    }
                }
            }

            // ---- 2. 清空根目录中两组轨道的文件，准备恢复目标 ----
            foreach (var f in TrackFiles)
            {
                var p = Path.Combine(gameDir, f);
                if (File.Exists(p)) File.Delete(p);
            }

            // ---- 3. 恢复目标轨道 ----
            if (target == TrackGs)
            {
                var srcDir = BackupDir(gameDir, TrackGs);
                foreach (var f in new[] { "dinput8.dll", "WoWTranslate335.dll", "dlls.txt" })
                {
                    var src = Path.Combine(srcDir, f);
                    if (!File.Exists(src))
                    {
                        lines.Add($"❌ GS 备份缺少 {f}（{srcDir}）。无法切回 Track A，请人工恢复。");
                        Rollback(gameDir, status.Current, directAssetsDir, lines);
                        return lines;
                    }
                    File.Copy(src, Path.Combine(gameDir, f));
                }
                lines.Add("✔ 已恢复 Track A（GS 插件：dinput8.dll + WoWTranslate335.dll + dlls.txt）");
            }
            else
            {
                var srcDir = status.DirectAssetsDir;
                foreach (var f in new[] { "dinput8.dll", "WoWTranslateDirect.dll" })
                    File.Copy(Path.Combine(srcDir, f), Path.Combine(gameDir, f));
                File.WriteAllText(Path.Combine(gameDir, "dlls.txt"),
                    "WoWTranslateDirect.dll" + Environment.NewLine);
                var cfgPath = WriteDirectConfig(gameDir, listenPort, displayMode, displayPrefix, outgoingMode);
                lines.Add($"✔ 已部署 Track B（Direct DLL，资产来自 {srcDir}）");
                lines.Add($"✔ 已写入 {Path.GetFileName(cfgPath)}（endpoint 127.0.0.1:{listenPort}，displayMode={displayMode}）");
                lines.Add("⚠ Track B 为实验状态：未经游戏内实测验证（对齐提交流铁律），出现异常请切回 Track A。");
            }

            // ---- 4. 写后自检 ----
            var after = Probe(gameDir, directAssetsDir);
            if (after.Current != target)
            {
                lines.Add($"❌ 切换后自检失败（实际 {after.Current}，期望 {target}），已回滚");
                Rollback(gameDir, status.Current, directAssetsDir, lines);
                return lines;
            }

            lines.Add($"✅ 切换完成：{status.CurrentText} → {after.CurrentText}。请完全重启游戏客户端生效。");
            return lines;
        }
        catch (Exception ex)
        {
            lines.Add($"❌ 切换失败：{ex.Message}");
            return lines;
        }
    }

    private static void Rollback(string gameDir, string previousTrack, string directAssetsDir,
        List<string> lines)
    {
        try
        {
            foreach (var f in TrackFiles)
            {
                var p = Path.Combine(gameDir, f);
                if (File.Exists(p)) File.Delete(p);
            }
            if (previousTrack == TrackGs)
            {
                var srcDir = BackupDir(gameDir, TrackGs);
                foreach (var f in new[] { "dinput8.dll", "WoWTranslate335.dll", "dlls.txt" })
                    if (File.Exists(Path.Combine(srcDir, f)))
                        File.Copy(Path.Combine(srcDir, f), Path.Combine(gameDir, f));
            }
            else if (previousTrack == TrackDirect)
            {
                var status = Probe(gameDir, directAssetsDir);
                if (status.DirectAssetsReady)
                {
                    foreach (var f in new[] { "dinput8.dll", "WoWTranslateDirect.dll" })
                        File.Copy(Path.Combine(status.DirectAssetsDir, f), Path.Combine(gameDir, f));
                    File.WriteAllText(Path.Combine(gameDir, "dlls.txt"), "WoWTranslateDirect.dll\n");
                }
            }
            lines.Add("  已尝试回滚到切换前状态");
        }
        catch (Exception ex)
        {
            lines.Add($"  回滚失败：{ex.Message}（请人工检查 {gameDir}）");
        }
    }
}
