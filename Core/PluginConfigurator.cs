using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core;

/// <summary>
/// WoWTranslate 插件一键配置（需求 8）。
///
/// 写入目标（优先级从高到低）：
///   1. WoWTranslate.ini（DLL 旁）：DLL 启动时优先读取，[provider] 指向本地代理端点；
///   2. SavedVariables/WoWTranslate.lua：面板配置（provider/openai*/语言/频道开关）；
///   3. WoWTranslate.lua：可选 Lua 补丁，在调用 DLL 翻译前注入 \1频道\1 标签，
///      让控制台获得频道信息（DLL 原生 API 不传频道，这是唯一搭车通道）。
///
/// 安全铁律（ARCHITECTURE_V2 R3）：游戏运行时写 SavedVariables 必被客户端退出时
/// 整体覆盖——所以 Wow.exe 进程存在时拒绝执行，这是硬门禁不做绕过。
/// 所有写入前先做时间戳备份。
/// </summary>
public sealed class PluginConfigurator
{
    private readonly AppConfig _cfg;
    public PluginConfigurator(AppConfig cfg) => _cfg = cfg;

    public sealed record ConfigureReport(bool Ok, List<string> Lines);

    public static bool IsWowRunning()
    {
        foreach (var name in new[] { "Wow", "Wow-64", "WowClassic", "wow" })
        {
            if (Process.GetProcessesByName(name).Length > 0) return true;
        }
        return false;
    }

    public ConfigureReport Configure(string gameDir, bool patchLuaChannelTag, bool syncIncomingChannels)
    {
        var lines = new List<string>();
        try
        {
            if (IsWowRunning())
            {
                lines.Add("❌ 检测到 Wow.exe 正在运行。游戏运行时写 SavedVariables 会在客户端退出时被覆盖，已拒绝执行。请先退出游戏再一键配置。");
                return new ConfigureReport(false, lines);
            }

            var addonDir = Path.Combine(gameDir, "Interface", "AddOns", "WoWTranslate");
            if (!Directory.Exists(addonDir))
            {
                lines.Add($"❌ 未找到插件目录：{addonDir}");
                return new ConfigureReport(false, lines);
            }
            lines.Add($"✔ 插件目录：{addonDir}");

            // ---- 1. WoWTranslate.ini（DLL 旁）----
            WriteIni(gameDir, lines);

            // ---- 2. SavedVariables ----
            var svPath = FindSavedVariables(gameDir);
            if (svPath == null)
            {
                lines.Add("⚠ 未找到 SavedVariables/WoWTranslate.lua（先登录一次游戏生成），面板配置跳过；INI 已生效，DLL 侧配置优先级更高。");
            }
            else
            {
                WriteSavedVariables(svPath, syncIncomingChannels, lines);
            }

            // ---- 3. Lua 补丁：频道标签 + 语言启发式 ----
            if (patchLuaChannelTag)
            {
                PatchLuaChannelTag(Path.Combine(addonDir, "WoWTranslate.lua"), lines);
                PatchLuaLanguageHeuristic(Path.Combine(addonDir, "WoWTranslate.lua"), lines);
            }

            lines.Add("✅ 一键配置完成。请完全重启游戏客户端后生效（ADR：部署后必须重启客户端）。");
            return new ConfigureReport(true, lines);
        }
        catch (Exception ex)
        {
            lines.Add($"❌ 配置失败：{ex.Message}");
            return new ConfigureReport(false, lines);
        }
    }

    // ==================== INI ====================

    private void WriteIni(string gameDir, List<string> lines)
    {
        var iniPath = Path.Combine(gameDir, "WoWTranslate.ini");
        var sb = new StringBuilder();
        sb.AppendLine("[provider]");
        sb.AppendLine("type = openai");
        sb.AppendLine($"endpoint = http://127.0.0.1:{_cfg.ListenPort}/v1/chat/completions");
        sb.AppendLine("model = WoWTranslateControl");
        sb.AppendLine("api_key = wtc-local");
        var content = sb.ToString();

        BackupIfExists(iniPath, lines);
        File.WriteAllText(iniPath, content, new UTF8Encoding(false));
        lines.Add($"✔ 已写入 {Path.GetFileName(iniPath)}：[provider] → http://127.0.0.1:{_cfg.ListenPort}/v1/chat/completions");
    }

    // ==================== SavedVariables ====================

    private static string? FindSavedVariables(string gameDir)
    {
        // Account 目录下可能有多个账号，改所有账号的 WoWTranslate.lua
        var accountRoot = Path.Combine(gameDir, "WTF", "Account");
        if (!Directory.Exists(accountRoot)) return null;
        string? first = null;
        foreach (var acct in Directory.GetDirectories(accountRoot))
        {
            var p = Path.Combine(acct, "SavedVariables", "WoWTranslate.lua");
            if (File.Exists(p)) { first ??= p; }
        }
        return first;
    }

    /// <summary>改所有已存在账号的 SV 文件（多账号同配）。</summary>
    private void WriteSavedVariables(string firstPath, bool syncIncomingChannels, List<string> lines)
    {
        var accountRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(firstPath)))!;
        foreach (var acct in Directory.GetDirectories(accountRoot))
        {
            var p = Path.Combine(acct, "SavedVariables", "WoWTranslate.lua");
            if (!File.Exists(p)) continue;
            BackupIfExists(p, lines);
            var text = File.ReadAllText(p);
            text = RewriteSavedVariables(text, _cfg.ListenPort, syncIncomingChannels,
                GetIncomingChannelToggles());
            File.WriteAllText(p, text, new UTF8Encoding(false));
            lines.Add($"✔ 已更新 {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p)))}\\SavedVariables\\WoWTranslate.lua");
        }
    }

    private Dictionary<string, bool> GetIncomingChannelToggles() => new()
    {
        ["SAY"] = _cfg.ChannelSay,
        ["YELL"] = _cfg.ChannelYell,
        ["WHISPER"] = _cfg.ChannelWhisper,
        ["PARTY"] = _cfg.ChannelParty,
        ["GUILD"] = _cfg.ChannelGuild,
        ["RAID"] = _cfg.ChannelRaid,
        ["BATTLEGROUND"] = _cfg.ChannelBattleground,
        ["CHANNEL"] = _cfg.ChannelWorld,
    };

    /// <summary>键级正则重写，未知键（WoWTranslateCache 等）原样保留。纯函数，可单测。</summary>
    public static string RewriteSavedVariables(string luaText, int listenPort,
        bool syncIncomingChannels, Dictionary<string, bool>? incomingChannels)
    {
        string Str(string text, string key, string value) =>
            Regex.Replace(text, $@"(\[""{key}""\]\s*=\s*)""[^""\\]*""",
                $"${{1}}\"{value}\"");

        // 键缺失时插入 WoWTranslateDB 表头之后（首次配置/精简 SV 场景）
        string Ensure(string text, string key, string luaValue)
        {
            if (Regex.IsMatch(text, $@"\[\""{key}\""\]\s*=")) return text;
            var insertAt = text.IndexOf("WoWTranslateDB = {", StringComparison.Ordinal);
            if (insertAt < 0) return text;
            var lineEnd = text.IndexOf('\n', insertAt);
            if (lineEnd < 0) return text;
            return text[..(lineEnd + 1)] + $"\t[\"{key}\"] = {luaValue},\n" + text[(lineEnd + 1)..];
        }

        var t = luaText;
        t = Ensure(t, "provider", "\"google_free\"");
        t = Ensure(t, "openaiEndpoint", "\"\"");
        t = Ensure(t, "openaiModel", "\"gpt-4.1-mini\"");
        t = Ensure(t, "openaiApiKey", "\"\"");
        t = Ensure(t, "incomingFromLang", "\"en\"");
        t = Ensure(t, "incomingToLang", "\"zh\"");
        t = Ensure(t, "outgoingToLang", "\"zh\"");
        t = Ensure(t, "enabled", "false");
        t = Ensure(t, "translateMode", "\"manual\"");
        t = Str(t, "provider", "openai");
        t = Str(t, "openaiEndpoint", $"http://127.0.0.1:{listenPort}/v1/chat/completions");
        t = Str(t, "openaiModel", "WoWTranslateControl");
        t = Str(t, "openaiApiKey", "wtc-local");
        t = Str(t, "incomingFromLang", "en");
        t = Str(t, "incomingToLang", "zh");
        t = Str(t, "outgoingToLang", "zh");

        t = Regex.Replace(t, @"(\[""enabled""\]\s*=\s*)(true|false)", "${1}true");
        t = Regex.Replace(t, @"(\[""translateMode""\]\s*=\s*)""[^""\\]*""", "${1}\"auto\"");

        if (syncIncomingChannels && incomingChannels != null)
            t = RewriteIncomingChannels(t, incomingChannels);
        return t;
    }

    /// <summary>只改 ["incomingChannels"] 块内部的布尔值，块外同名词不受影响。</summary>
    public static string RewriteIncomingChannels(string luaText, Dictionary<string, bool> toggles)
    {
        const string marker = "[\"incomingChannels\"]";
        var start = luaText.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return luaText;
        var open = luaText.IndexOf('{', start);
        if (open < 0) return luaText;
        var depth = 0;
        var close = -1;
        for (var i = open; i < luaText.Length; i++)
        {
            if (luaText[i] == '{') depth++;
            else if (luaText[i] == '}')
            {
                depth--;
                if (depth == 0) { close = i; break; }
            }
        }
        if (close < 0) return luaText;

        var block = luaText[start..(close + 1)];
        foreach (var (ch, on) in toggles)
        {
            block = Regex.Replace(block,
                $@"(\[""{ch}""\]\s*=\s*)(true|false)",
                $"${{1}}{(on ? "true" : "false")}");
        }
        return luaText[..start] + block + luaText[(close + 1)..];
    }

    // ==================== Lua 频道标签补丁 ====================

    /// <summary>
    /// 把 DLL 翻译调用改为携带频道标签：\1&lt;频道&gt;\1 + 原文本。
    /// 控制台剥标签后按频道过滤；译文不含标签，插件展示不受影响。
    /// 幂等：已打补丁的文件跳过。
    /// </summary>
    public static void PatchLuaChannelTag(string luaPath, List<string> lines)
    {
        if (!File.Exists(luaPath))
        {
            lines.Add($"⚠ 未找到 {luaPath}，Lua 频道补丁跳过");
            return;
        }

        var text = File.ReadAllText(luaPath);
        const string target = "WoWTranslate_API.Translate(textToTranslate, function(translation, err)";
        const string patchedMark = "\\1\" .. (currentIncomingChannel or \"OTHER\") .. \"\\1";
        const string replacement =
            "WoWTranslate_API.Translate(\"\\1\" .. (currentIncomingChannel or \"OTHER\") .. \"\\1\" .. textToTranslate, function(translation, err)";

        if (text.Contains(patchedMark))
        {
            lines.Add("✔ WoWTranslate.lua 已带频道标签补丁，跳过");
            return;
        }

        var count = Regex.Matches(text, Regex.Escape(target)).Count;
        if (count != 1)
        {
            lines.Add($"⚠ WoWTranslate.lua 调用点匹配数异常（{count}，期望 1），Lua 补丁跳过——插件文件版本可能已变化，请人工确认");
            return;
        }

        var dir = Path.GetDirectoryName(luaPath)!;
        var name = Path.GetFileName(luaPath);
        var backup = Path.Combine(dir, $"{name}.bak_{DateTime.Now:yyyyMMdd_HHmmss}");
        File.Copy(luaPath, backup, overwrite: true);

        File.WriteAllText(luaPath, text.Replace(target, replacement), new UTF8Encoding(false));

        // 写后自检：补丁标记必须存在，否则回滚
        var after = File.ReadAllText(luaPath);
        if (!after.Contains(patchedMark))
        {
            File.Copy(backup, luaPath, overwrite: true);
            lines.Add("❌ Lua 补丁写入自检失败，已回滚备份");
            return;
        }
        lines.Add($"✔ WoWTranslate.lua 已注入频道标签补丁（备份 {Path.GetFileName(backup)}）");
    }

    // ==================== Lua 语言启发式补丁 ====================

    /// <summary>
    /// 修插件端语言启发式漏网：插件判断"是不是英文"时直接数字母，而它给物品链接生成的
    /// <c>|cff........http://ph.wt/N|r</c> 包装自带 8+ 个 ASCII 字母，导致纯中文句子
    /// （如"[HC] 我觉得是血"剥占位符后只剩 2 个字母 HC）被判成英文送去翻译。
    /// 修复：ContainsSourceLanguage 判语言前剥离颜色包装与占位符（2026-09-11 游戏实测）。
    /// 幂等：已打补丁的文件跳过。
    /// </summary>
    public static void PatchLuaLanguageHeuristic(string luaPath, List<string> lines)
    {
        if (!File.Exists(luaPath))
        {
            lines.Add($"⚠ 未找到 {luaPath}，Lua 语言补丁跳过");
            return;
        }

        var text = File.ReadAllText(luaPath);
        const string patchMark = "WTC语言补丁";
        const string target = "    return ContainsLanguageChars(text, sourceLang)";
        const string replacement =
            "    -- WTC语言补丁：剥离颜色包装与链接占位符后再判语言，纯中文不再被骗成英文\n" +
            "    text = string.gsub(text, \"|c%x%x%x%x%x%x%x%x.-|r\", \"\")\n" +
            "    text = string.gsub(text, \"http://ph%.wt/%d+\", \"\")\n" +
            "    return ContainsLanguageChars(text, sourceLang)";

        if (text.Contains(patchMark))
        {
            lines.Add("✔ WoWTranslate.lua 已带语言启发式补丁，跳过");
            return;
        }

        var count = Regex.Matches(text, Regex.Escape(target)).Count;
        if (count != 1)
        {
            lines.Add($"⚠ ContainsSourceLanguage 返回点匹配数异常（{count}，期望 1），语言补丁跳过——插件文件版本可能已变化，请人工确认");
            return;
        }

        var dir = Path.GetDirectoryName(luaPath)!;
        var name = Path.GetFileName(luaPath);
        var backup = Path.Combine(dir, $"{name}.bak_{DateTime.Now:yyyyMMdd_HHmmss}");
        File.Copy(luaPath, backup, overwrite: true);

        File.WriteAllText(luaPath, text.Replace(target, replacement), new UTF8Encoding(false));

        // 写后自检：补丁标记必须存在，否则回滚
        var after = File.ReadAllText(luaPath);
        if (!after.Contains(patchMark))
        {
            File.Copy(backup, luaPath, overwrite: true);
            lines.Add("❌ 语言补丁写入自检失败，已回滚备份");
            return;
        }
        lines.Add($"✔ WoWTranslate.lua 已注入语言启发式补丁（备份 {Path.GetFileName(backup)}）");
    }

    private static void BackupIfExists(string path, List<string> lines)
    {
        if (!File.Exists(path)) return;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var backup = Path.Combine(dir, $"{name}.bak_{DateTime.Now:yyyyMMdd_HHmmss}");
        File.Copy(path, backup, overwrite: true);
        lines.Add($"  （备份 {Path.GetFileName(backup)}）");
    }
}
