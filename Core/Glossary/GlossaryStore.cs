using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WoWTranslateControl.Core.Glossary;

/// <summary>一条术语映射：源语言术语 → 目标语言术语。</summary>
public sealed record GlossaryEntry(int Id, string From, string To);

/// <summary>
/// 术语表权威存储（控制台侧单一事实源，ADR-003）。
/// JSON 持久化到 glossary.json；任何增删改都会使 Version 自增，
/// 该版本号参与缓存 key，术语变更后旧译文自动失效。
/// 线程安全：代理请求线程与 UI 线程并发访问。
/// </summary>
public sealed class GlossaryStore
{
    private readonly object _sync = new();
    private readonly List<GlossaryEntry> _entries = new();
    private int _nextId = 1;
    private long _version = 1;
    private readonly string _path;

    public GlossaryStore(string path) => _path = path;

    /// <summary>当前版本号，任何修改 +1。</summary>
    public long Version { get { lock (_sync) return _version; } }

    public int Count { get { lock (_sync) return _entries.Count; } }

    public List<GlossaryEntry> Snapshot()
    {
        lock (_sync) return new List<GlossaryEntry>(_entries);
    }

    public GlossaryEntry Add(string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0)
            throw new ArgumentException("术语的原文与译文都不能为空");

        lock (_sync)
        {
            var dup = _entries.FirstOrDefault(e =>
                string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase));
            if (dup != null) throw new InvalidOperationException($"术语已存在：{dup.Id} {dup.From}");

            var entry = new GlossaryEntry(_nextId++, from, to);
            _entries.Add(entry);
            _version++;
            SaveNoLock();
            return entry;
        }
    }

    public void Update(int id, string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0)
            throw new ArgumentException("术语的原文与译文都不能为空");

        lock (_sync)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) throw new InvalidOperationException($"术语不存在：id={id}");
            _entries[idx] = _entries[idx] with { From = from, To = to };
            _version++;
            SaveNoLock();
        }
    }

    public bool Remove(int id)
    {
        lock (_sync)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            _entries.RemoveAt(idx);
            _version++;
            SaveNoLock();
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _version++;
            SaveNoLock();
        }
    }

    // ---------- 持久化 ----------

    private sealed record Dto(long Version, int NextId, List<GlossaryEntry> Entries);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static GlossaryStore Load(string path)
    {
        var store = new GlossaryStore(path);
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
                if (dto != null)
                {
                    store._entries.AddRange(dto.Entries ?? new List<GlossaryEntry>());
                    store._version = Math.Max(1, dto.Version);
                    store._nextId = Math.Max(1, dto.NextId);
                }
            }
        }
        catch
        {
            // 术语表损坏时按空表启动，不让代理起不来
        }
        return store;
    }

    private void SaveNoLock()
    {
        try
        {
            var dto = new Dto(_version, _nextId, new List<GlossaryEntry>(_entries));
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOpts), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 保存失败不中断翻译服务
        }
    }

    public void Save()
    {
        lock (_sync) SaveNoLock();
    }

    // ---------- CSV 导入导出 ----------

    /// <summary>导入 CSV（每行：原文,译文；支持 UTF-8 BOM；跳过表头与空行）。</summary>
    public (int added, int skipped) ImportCsv(string path)
    {
        var added = 0;
        var skipped = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (line.StartsWith("原文", StringComparison.OrdinalIgnoreCase) && line.Contains("译文")) continue;
            var parts = line.Split(',');
            if (parts.Length < 2) { skipped++; continue; }
            var from = parts[0].Trim();
            var to = parts[1].Trim();
            if (from.Length == 0 || to.Length == 0) { skipped++; continue; }
            lock (_sync)
            {
                if (_entries.Any(e => string.Equals(e.From, from, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                _entries.Add(new GlossaryEntry(_nextId++, from, to));
                added++;
            }
        }
        if (added > 0)
        {
            lock (_sync)
            {
                _version++;
                SaveNoLock();
            }
        }
        return (added, skipped);
    }

    public void ExportCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("原文,译文");
        foreach (var e in Snapshot())
            sb.Append(EscapeCsv(e.From)).Append(',').AppendLine(EscapeCsv(e.To));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM 方便 Excel 打开
    }

    private static string EscapeCsv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
