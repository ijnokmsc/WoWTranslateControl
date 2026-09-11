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
///   1. WoW 超链接/占位符（http://ph.wt/N、|H...|h...|h、|T...|t、|c...|r）绝不参与
///      术语替换——先整体摘出为 ⟦Pn⟧ 保护段，替换完再原样放回；
///   2. 占位符使用 ⟦ ⟧ (U+27E6/27E7)，与游戏文本几乎不可能撞车；
///   3. 长术语优先替换（"Dragon's Breath" 不能被 "Dragon" 先吃掉）；
///   4. 模型若吞掉占位符，Restore 找不到时静默放弃该条术语，不影响其余内容。
/// </summary>
public static class GlossaryApplier
{
    private static readonly Regex ReProtected = new(
        @"https?://ph\.wt/\d+|\|H[^|]*\|h[^|]*\|h|\|T[^|]*\|t|\|c[0-9a-fA-F]{8}|\|r",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string PlaceholderPrefix = "⟦G";
    public const string PlaceholderSuffix = "⟧";

    /// <summary>
    /// 替换前：文本 → (替换后文本, 占位符→目标语术语 映射)。
    /// </summary>
    public static (string Text, Dictionary<string, string> Map) Apply(
        string text, IReadOnlyList<GlossaryEntry> entries)
    {
        var map = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text) || entries.Count == 0)
            return (text, map);

        // 1) 保护 WoW 标记段
        var protectedSpans = new List<string>();
        string working = ReProtected.Replace(text, m =>
        {
            protectedSpans.Add(m.Value);
            return $"⟦P{protectedSpans.Count - 1}⟧";
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

        // 3) 只保留真正发生替换的条目，减少提示词噪音
        map = map.Where(kv => working.Contains(kv.Key))
                 .ToDictionary(kv => kv.Key, kv => kv.Value);

        // 4) 还原保护段
        if (protectedSpans.Count > 0)
        {
            working = Regex.Replace(working, @"⟦P(\d+)⟧", m =>
            {
                var i = int.Parse(m.Groups[1].Value);
                return i < protectedSpans.Count ? protectedSpans[i] : m.Value;
            });
        }

        return (working, map);
    }

    /// <summary>
    /// 替换后：把译文中的 ⟦G{id}⟧ 还原为目标语术语。
    /// 返回值同时报告是否所有占位符都被成功还原。
    /// </summary>
    public static (string Text, bool AllRestored) Restore(
        string translated, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0 || string.IsNullOrEmpty(translated))
            return (translated, true);

        var allRestored = true;
        foreach (var kv in map)
        {
            if (translated.Contains(kv.Key))
                translated = translated.Replace(kv.Key, kv.Value);
        }

        // 兜底：若仍有占位符残留（模型篡改），原样保留术语的源语言文本
        if (translated.Contains(PlaceholderPrefix))
        {
            translated = Regex.Replace(
                translated,
                @"⟦G(\d+)⟧",
                m => map.TryGetValue(m.Value, out var v) ? v : m.Value);
            allRestored = !translated.Contains(PlaceholderPrefix);
        }

        return (translated, allRestored);
    }

    /// <summary>附加到 system prompt 的占位符说明（仅当本条发生替换时追加）。</summary>
    public const string PromptAddendum =
        " The text contains placeholder tokens like ⟦G12⟧ marking protected game terms. " +
        "Keep every ⟦G...⟧ token EXACTLY as-is in your output, do not translate, merge, " +
        "reorder or drop them.";
}
