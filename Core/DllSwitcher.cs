using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WoWTranslateControl.Core;

/// <summary>
/// Direct DLL 部署器（3.0 单轨化：GS 插件轨道已移除，本文档历史见 ARCHITECTURE_V2.md ADR-007）。
///
/// 唯一轨道 = 自有 Direct DLL（全自治驱动）：dinput8.dll + WoWTranslateDirect.dll + dlls.txt
///
/// 文件语义（游戏根目录）：
///   - 部署时备份首次基线到 wtc_backup/direct/（不覆盖已有备份）；
///   - DLL 资产来源：控制台 assets/direct-dll/（或游戏目录 wtc_direct_dll/），
///     资产未就绪时拒绝部署；
///   - 部署后 Probe 自检，失败自动回滚。
///
/// 注意：老 GS 插件的 WoWTranslate335.dll 不在本轨道文件清单内——Direct 的
/// dinput8.dll 覆盖同一入口后它无人加载、自然失活；本类不删除用户的其他文件。
///
/// 安全铁律：Wow.exe 运行时拒绝执行（DLL 被占用 + 避免半部署状态）。
/// </summary>
public sealed class DllSwitcher
{
    public const string TrackDirect = "direct";

    /// <summary>本轨道涉及的文件名（相对游戏根目录）。</summary>
    private static readonly string[] TrackFiles =
    {
        "dinput8.dll", "WoWTranslateDirect.dll", "dlls.txt",
    };

    public sealed record SwitchStatus(
        string Current,             // "direct" | "disabled" | "unknown"
        string CurrentText,
        bool DirectAssetsReady,
        string DirectAssetsDir,
        List<string> Details);

    // ==================== 探测 ====================

    public static SwitchStatus Probe(string gameDir, string directAssetsDir)
    {
        var details = new List<string>();

        var hasDinput = File.Exists(Path.Combine(gameDir, "dinput8.dll"));
        var hasDirect = File.Exists(Path.Combine(gameDir, "WoWTranslateDirect.dll"));

        // 目录有效性提示（不改变判定，但让配错目录一眼可见）
        if (!File.Exists(Path.Combine(gameDir, "Wow.exe")))
            details.Add($"⚠ 该目录没有 Wow.exe，可能不是客户端根目录：{gameDir}");

        var (ready, assetDir) = FindDirectAssets(gameDir, directAssetsDir);
        var assetLabel = assetDir == directAssetsDir ? "控制台 assets\\direct-dll" : "游戏目录 wtc_direct_dll";
        if (ready)
            details.Add($"Direct 资产就绪：{assetLabel}");
        else
            details.Add($"Direct 资产未就绪（需在 {assetLabel} 放置 dinput8.dll + WoWTranslateDirect.dll）");

        string cur, text;
        if (hasDinput && hasDirect) { cur = TrackDirect; text = "Direct DLL — 自治引擎（已部署）"; }
        else if (!hasDinput) { cur = "disabled"; text = "未启用（游戏根目录无 dinput8.dll）"; }
        else { cur = "unknown"; text = "未知状态（dinput8.dll 存在但未发现 WoWTranslateDirect.dll）"; }

        if (Directory.Exists(BackupDir(gameDir)))
            details.Add("Direct 备份存在：wtc_backup/direct");
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

    private static string BackupDir(string gameDir) =>
        Path.Combine(gameDir, "wtc_backup", TrackDirect);

    // ==================== 配置（v16 全自治驱动） ====================

    /// <summary>
    /// 写游戏根目录 WoWTranslateDirect.json（Direct DLL v14 起启动时读取）。
    /// endpoint 指向控制台伪装 OpenAI 端点，displayMode/displayPrefix 由内嵌驱动 Lua 消费。
    /// UTF-8 无 BOM（DLL 侧 nlohmann::json 不认 BOM）。
    /// </summary>
    public static string WriteDirectConfig(string gameDir, int listenPort,
        string displayMode, string displayPrefix, string outgoingMode = "off",
        bool dllLog = false, string outFilter = "", string outOff = "")
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
            $"  \"outgoingMode\": \"{outgoingMode}\",\n" +
            $"  \"outFilter\": \"{EscapeJson(outFilter ?? "")}\",\n" +
            $"  \"outOff\": \"{EscapeJson(outOff ?? "")}\",\n" +
            $"  \"log\": {(dllLog ? "true" : "false")}\n" +
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

    // ==================== 部署 ====================

    /// <summary>Wow.exe 进程探测（可注入）。</summary>
    public static Func<bool> WowRunningProbe { get; set; } = IsWowRunning;

    public static bool IsWowRunning()
    {
        foreach (var name in new[] { "Wow", "Wow-64", "WowClassic", "wow" })
        {
            if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) return true;
        }
        return false;
    }

    /// <summary>部署 Direct DLL 到游戏目录（重复调用为幂等重部署）。 Wow.exe 运行时拒绝。</summary>
    public static List<string> DeployDirect(string gameDir, string directAssetsDir,
        Func<bool>? wowRunning = null,
        int listenPort = 8080,
        string displayMode = "replace",
        string displayPrefix = "[译]",
        string outgoingMode = "off",
        bool dllLog = false,
        string outFilter = "",
        string outOff = "")
    {
        var lines = new List<string>();
        try
        {
            if (wowRunning?.Invoke() ?? WowRunningProbe())
            {
                lines.Add("❌ 检测到 Wow.exe 正在运行。部署 DLL 需要游戏完全退出（文件被占用 + 避免半部署状态）。");
                return lines;
            }

            if (!Directory.Exists(gameDir))
            {
                lines.Add($"❌ 游戏目录不存在：{gameDir}");
                return lines;
            }

            if (!File.Exists(Path.Combine(gameDir, "Wow.exe")))
            {
                lines.Add($"❌ 目录中未找到 Wow.exe，拒绝部署：{gameDir}");
                lines.Add("   请在「游戏目录」框填入 3.3.5 客户端根目录（可点右侧「浏览…」手动选择）。");
                return lines;
            }

            var status = Probe(gameDir, directAssetsDir);
            lines.Add($"当前状态：{status.CurrentText}");

            if (!status.DirectAssetsReady)
            {
                lines.Add($"❌ Direct 资产未就绪：{status.DirectAssetsDir} 中缺少 dinput8.dll / WoWTranslateDirect.dll。");
                return lines;
            }

            // ---- 1. 首次基线备份（不覆盖，避免坏状态覆盖好备份）----
            var bakDir = BackupDir(gameDir);
            Directory.CreateDirectory(bakDir);
            foreach (var f in TrackFiles)
            {
                var src = Path.Combine(gameDir, f);
                var dst = Path.Combine(bakDir, f);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst, overwrite: false);
                    lines.Add($"  已备份 {f} → wtc_backup/direct/{f}");
                }
            }

            // ---- 2. 清掉本轨道旧文件 ----
            foreach (var f in TrackFiles)
            {
                var p = Path.Combine(gameDir, f);
                if (File.Exists(p)) File.Delete(p);
            }

            // ---- 3. 部署 ----
            var srcDir = status.DirectAssetsDir;
            foreach (var f in new[] { "dinput8.dll", "WoWTranslateDirect.dll" })
                File.Copy(Path.Combine(srcDir, f), Path.Combine(gameDir, f));
            File.WriteAllText(Path.Combine(gameDir, "dlls.txt"),
                "WoWTranslateDirect.dll" + Environment.NewLine);
            var cfgPath = WriteDirectConfig(gameDir, listenPort, displayMode, displayPrefix,
                outgoingMode, dllLog, outFilter, outOff);
            lines.Add($"✔ 已部署 Direct DLL（资产来自 {srcDir}）");
            lines.Add($"✔ 已写入 {Path.GetFileName(cfgPath)}（endpoint 127.0.0.1:{listenPort}，displayMode={displayMode}，outgoing={outgoingMode}）");

            // ---- 4. 写后自检 ----
            var after = Probe(gameDir, directAssetsDir);
            if (after.Current != TrackDirect)
            {
                lines.Add($"❌ 部署后自检失败（实际 {after.Current}），请人工检查 {gameDir}");
                return lines;
            }

            lines.Add("✅ 部署完成。请完全重启游戏客户端生效。");
            return lines;
        }
        catch (Exception ex)
        {
            lines.Add($"❌ 部署失败：{ex.Message}");
            return lines;
        }
    }


    /// <summary>
    /// 自动部署：未部署（disabled/unknown）时静默部署；
    /// 已部署则只做版本自愈比对。Wow.exe 运行时不动文件。
    /// </summary>
    public static List<string> EnsureDeployed(string gameDir, string directAssetsDir,
        string displayMode, string displayPrefix, string outgoingMode,
        int listenPort = 8080, Func<bool>? wowRunning = null, bool dllLog = false,
        string outFilter = "", string outOff = "")
    {
        var status = Probe(gameDir, directAssetsDir);
        if (status.Current == TrackDirect)
            return EnsureUpToDate(gameDir, directAssetsDir, wowRunning);

        var lines = new List<string>();
        if (wowRunning?.Invoke() ?? WowRunningProbe())
        {
            lines.Add("ℹ 检测到 Wow.exe 正在运行，自动部署跳过（关闭游戏后重新打开控制台即可）。");
            return lines;
        }
        if (!Directory.Exists(gameDir) || !File.Exists(Path.Combine(gameDir, "Wow.exe")))
        {
            lines.Add("ℹ 游戏目录无效，自动部署跳过。");
            return lines;
        }
        lines.Add("ℹ 未检测到 Direct DLL，正在自动部署…");
        lines.AddRange(DeployDirect(gameDir, directAssetsDir, wowRunning,
            listenPort, displayMode, displayPrefix, outgoingMode, dllLog, outFilter, outOff));
        return lines;
    }

    /// <summary>
    /// 启动时版本自愈：已部署且游戏未运行时，比对游戏目录两个 DLL 与本地
    /// 资产的 MD5，不一致（控制台更新后忘了重新部署/手工拷贝错版本）则自动重部署。
    /// </summary>
    public static List<string> EnsureUpToDate(string gameDir, string directAssetsDir,
        Func<bool>? wowRunning = null)
    {
        var lines = new List<string>();
        try
        {
            var status = Probe(gameDir, directAssetsDir);
            if (status.Current != TrackDirect || !status.DirectAssetsReady)
                return lines;   // 未部署：不做校验

            if (wowRunning?.Invoke() ?? WowRunningProbe())
            {
                lines.Add("ℹ 检测到 Wow.exe 正在运行，DLL 版本校验跳过（下次启动控制台时再比对）。");
                return lines;
            }

            var drifted = new List<string>();
            foreach (var f in new[] { "dinput8.dll", "WoWTranslateDirect.dll" })
            {
                var game = Path.Combine(gameDir, f);
                var asset = Path.Combine(status.DirectAssetsDir, f);
                if (!File.Exists(game) || !File.Exists(asset)) continue;
                if (!SameHash(game, asset)) drifted.Add(f);
            }

            if (drifted.Count == 0) return lines;

            // 备份策略：有备份就直接覆盖，没有先备份当前版本——
            // 首次备份 = 原始版本，之后无需判断 DLL 归属
            var bak = BackupDir(gameDir);
            Directory.CreateDirectory(bak);
            foreach (var f in drifted)
            {
                var bakFile = Path.Combine(bak, f);
                if (!File.Exists(bakFile))
                {
                    File.Copy(Path.Combine(gameDir, f), bakFile);
                    lines.Add($"  已备份 {f} → wtc_backup/direct/（首次）");
                }
                File.Copy(Path.Combine(status.DirectAssetsDir, f), Path.Combine(gameDir, f), overwrite: true);
            }
            lines.Add($"✔ 游戏目录 DLL 与本地资产版本不一致（{string.Join("、", drifted)}），已自动重新部署。" +
                      "下次启动游戏生效。");
        }
        catch (Exception ex)
        {
            lines.Add($"⚠ DLL 版本校验失败：{ex.Message}");
        }
        return lines;
    }

    private static bool SameHash(string a, string b)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return Convert.ToHexString(md5.ComputeHash(fa)) == Convert.ToHexString(md5.ComputeHash(fb));
    }

    // ==================== 卸载（v41 智能卸载） ====================

    /// <summary>备份文件是否含 WTC 标识字符串（"WoWTranslateDirect"）——有即 WTC 自己的产物，不还原。</summary>
    private static bool ContainsWtcMark(string file)
    {
        const string mark = "WoWTranslateDirect";
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[64 * 1024];
            var tail = Array.Empty<byte>();
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                // 跨块边界保留 mark.Length-1 字节，避免标识恰好骑在分界处漏检
                var chunk = new byte[tail.Length + n];
                tail.CopyTo(chunk, 0);
                Array.Copy(buf, 0, chunk, tail.Length, n);
                if (System.Text.Encoding.Latin1.GetString(chunk).Contains(mark)) return true;
                tail = chunk[^Math.Min(mark.Length - 1, chunk.Length)..];
            }
        }
        catch { return true; }   // 读不了按 WTC 处理（不还原，保守）
        return false;
    }

    /// <summary>
    /// 智能卸载：删除本轨道文件与 WoWTranslateDirect.json；wtc_backup/direct 中
    /// 归属不是 WTC 的备份文件还原回游戏根目录（部署前用户原有的文件，如其他模组
    /// 的 dinput8.dll）。归属判定：MD5 等于当前 WTC 资产、或文件内含 WTC 标识
    /// 字符串（覆盖 EnsureUpToDate 备份过的旧版 WTC DLL）→ 是 WTC 的，不还原；
    /// dlls.txt 按 WTC 固定写入内容特判。备份目录保留。Wow.exe 运行时拒绝。
    /// </summary>
    public static List<string> UninstallDirect(string gameDir, string directAssetsDir,
        Func<bool>? wowRunning = null)
    {
        var lines = new List<string>();
        try
        {
            if (wowRunning?.Invoke() ?? WowRunningProbe())
            {
                lines.Add("❌ 检测到 Wow.exe 正在运行。卸载需要游戏完全退出（文件被占用）。");
                return lines;
            }
            if (!Directory.Exists(gameDir))
            {
                lines.Add($"❌ 游戏目录不存在：{gameDir}");
                return lines;
            }

            var status = Probe(gameDir, directAssetsDir);
            lines.Add($"当前状态：{status.CurrentText}");
            if (status.Current != TrackDirect)
            {
                lines.Add("ℹ 未检测到已部署的 Direct DLL，无需卸载。");
                return lines;
            }

            // ---- 1. 删除本轨道文件 + 配置 ----
            foreach (var f in TrackFiles)
            {
                var p = Path.Combine(gameDir, f);
                if (File.Exists(p)) { File.Delete(p); lines.Add($"  已删除 {f}"); }
            }
            var cfg = Path.Combine(gameDir, "WoWTranslateDirect.json");
            if (File.Exists(cfg)) { File.Delete(cfg); lines.Add("  已删除 WoWTranslateDirect.json"); }

            // ---- 2. 备份还原：只还原归属不是 WTC 的文件 ----
            var bakDir = BackupDir(gameDir);
            if (Directory.Exists(bakDir))
            {
                foreach (var bakFile in Directory.GetFiles(bakDir))
                {
                    var name = Path.GetFileName(bakFile);
                    var dst = Path.Combine(gameDir, name);
                    string? restoreReason = null;
                    if (name == "dlls.txt")
                    {
                        if (File.ReadAllText(bakFile).Trim() != "WoWTranslateDirect.dll")
                            restoreReason = "内容非 WTC 写入格式";
                    }
                    else if (File.Exists(dst))
                    {
                        lines.Add($"  跳过 {name}（游戏根目录已存在同名文件，不覆盖）");
                    }
                    else
                    {
                        var asset = Path.Combine(directAssetsDir, name);
                        var isWtcOwn = (File.Exists(asset) && SameHash(bakFile, asset)) ||
                                       ContainsWtcMark(bakFile);
                        if (!isWtcOwn) restoreReason = "用户原有文件";
                    }
                    if (restoreReason != null)
                    {
                        File.Copy(bakFile, dst, overwrite: false);
                        lines.Add($"  已还原 {name} ← wtc_backup/direct（{restoreReason}）");
                    }
                }
                lines.Add("ℹ 备份目录 wtc_backup/direct 保留未动。");
            }

            lines.Add("✅ 卸载完成。下次启动游戏不再加载翻译引擎；点「重新部署 Direct DLL」可随时恢复。");
            return lines;
        }
        catch (Exception ex)
        {
            lines.Add($"❌ 卸载失败：{ex.Message}");
            return lines;
        }
    }
}
