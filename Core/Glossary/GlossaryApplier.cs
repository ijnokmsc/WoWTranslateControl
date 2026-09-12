using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WoWTranslateControl.Core.Glossary;

/// <summary>
/// 术语应用器：把待译文本中的术语替换为占位符 ⟦G{id}⟧ 送模型，
/// 译文回来后把占位符还原为目标语术语。
///
/// 关键约束：
///   1. WoW 超链接/占位符（http://ph.wt/N、|H...|h...|h、|T...|t、|c...|r）绝不送模型：
///      摘出为 ⟦Pn⟧ 保护段并【穿透整个模型调用】，译文回来后原样放回。
///      2026-09-11 游戏实测教训：曾把保护段在送模型前还原——模型看到
///      "|cff0070DDhttp://ph.wt/1|r ya" 会篡改标记（译文泄漏 |cff0070、
///      把系统标签 [HC] 翻成 [英雄模式]）。模型只应看到 ⟦P0⟧ ya。
///   2. 占位符使用 ⟦ ⟧ (U+27E6/27E7)，与游戏文本几乎不可能撞车；
///   3. 长术语优先替换（"Dragon's Breath" 不能被 "Dragon" 先吃掉）；
///   4. 模型若吞掉占位符，Restore 找不到时静默放弃该条，不影响其余内容。
/// </summary>
public static class GlossaryApplier
{
    // 完整包裹形态（|c + 链接 + |r）必须排在零散形态之前，整体摘成一个 ⟦P⟧，
    // 否则模型会看到 |cff0070DD⟦P0⟧|r 这种碎片段落并篡改它。
    // 2026-09-12 追加：[HC]/[WB] 等全大写方括号短标签（服务器/模式标记）——模型会把
    // [HC] 翻成 [英雄的]，必须像链接一样原样穿透。(?-i) 防止 IgnoreCase 波及小写正文。
    private static readonly Regex ReProtected = new(
        @"\|c[0-9a-fA-F]{8}(?:\|H[^|]*\|h[^|]*\|h|https?://ph\.wt/\d+)\|r" +
        @"|https?://ph\.wt/\d+|\|H[^|]*\|h[^|]*\|h|\|T[^|]*\|t|\|c[0-9a-fA-F]{8}|\|r" +
        @"|(?-i:\[[A-Z0-9]{2,10}\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string PlaceholderPrefix = "⟦G";
    public const string PlaceholderSuffix = "⟧";

    private static readonly IReadOnlyList<GlossaryEntry> NoEntries =
        Array.Empty<GlossaryEntry>();

    /// <summary>
    /// 替换前：文本 → (送模型的文本, 占位符→还原值 映射)。
    /// 映射同时包含 ⟦G{id}⟧→目标语术语 与 ⟦Pn⟧→原保护段。
    /// </summary>
    public static (string Text, Dictionary<string, string> Map) Apply(
        string text, IReadOnlyList<GlossaryEntry> entries)
    {
        var map = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text))
            return (text, map);

        entries ??= NoEntries;

        // 1) 保护 WoW 标记段 → ⟦Pn⟧（穿透模型调用，译文回来后由 Restore 放回）
        var protectedSpans = new List<string>();
        var working = ReProtected.Replace(text, m =>
        {
            protectedSpans.Add(m.Value);
            var token = $"⟦P{protectedSpans.Count - 1}⟧";
            map[token] = m.Value;
            return token;
        });

        // 2) 长术语优先，逐条替换为 ⟦G{id}⟧
        foreach (var e in entries.OrderByDescending(e => e.From.Length))
        {
            if (e.From.Length == 0) continue;
            var placeholder = $"{PlaceholderPrefix}{e.Id}{PlaceholderSuffix}";
            // (?<!\w)/(?!\w) 而非 \b：\b 在术语首尾是标点时失效（如 "M+" 的 + 后无词边界）
            var pattern = e.From.Any(char.IsLetterOrDigit)
                ? $@"(?<!\w){Regex.Escape(e.From)}(?!\w)"
                : Regex.Escape(e.From);
            working = Regex.Replace(
                working, pattern, placeholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            map[placeholder] = e.To;
        }

        // 3) 只保留真正出现在送模型文本中的占位符，减少提示词噪音
        map = map.Where(kv => working.Contains(kv.Key))
                 .ToDictionary(kv => kv.Key, kv => kv.Value);

        return (working, map);
    }

    /// <summary>
    /// 替换后：把译文中的 ⟦G{id}⟧ 还原为目标语术语、⟦Pn⟧ 还原为原保护段。
    /// 返回值同时报告是否所有占位符都被成功还原。
    /// </summary>
    public static (string Text, bool AllRestored) Restore(
        string translated, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0 || string.IsNullOrEmpty(translated))
            return (translated, true);

        foreach (var kv in map)
        {
            if (translated.Contains(kv.Key))
                translated = translated.Replace(kv.Key, kv.Value);
        }

        // 兜底：模型篡改了 ⟦ ⟧ 字符时按编号正则救回
        if (translated.Contains('⟦'))
        {
            translated = Regex.Replace(
                translated,
                @"⟦[GP](\d+)⟧",
                m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
        }

        var allRestored = !translated.Contains('⟦');
        return (translated, allRestored);
    }

    /// <summary>附加到 system prompt 的占位符说明（仅当本条发生替换/保护时追加）。</summary>
    public const string PromptAddendum =
        " The text contains placeholder tokens like ⟦G12⟧ (game terms) and ⟦P0⟧ " +
        "(item links / UI markup). Keep every ⟦...⟧ token EXACTLY as-is in your " +
        "output, do not translate, merge, reorder or drop them.";
}
