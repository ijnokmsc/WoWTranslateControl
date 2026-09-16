using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WoWTranslateControl.Core.Glossary;
using WoWTranslateControl.Models;

namespace WoWTranslateControl.Core.Pipeline;

/// <summary>
/// 响应缓存阶段（3.0 双层缓存）。
///
/// 设计（ADR-003 + 3.0 双层改造）：
///   cache key = SHA256(原文 | 目标语 | 术语表版本 | ProviderId | systemPrompt哈希)
///   —— 术语表任何增删改都会 bump Version，旧译文自然失效，不存在"改了没效果"。
///
///   热层（tier 2）：命中次数 ≥ HotThreshold 的词条晋升，永不按容量淘汰，
///     只有手动清空才移除——世界频道高频短句不再被刷屏挤出。
///   温层（tier 1）：常规精确匹配层，按累计原文长度预算淘汰（取代旧的条数上限），
///     超预算时按 LastUsed 从旧到新淘汰 1/4。
///   查找顺序：热层 → 温层 →（未命中）进入后续阶段；命中都累加计数，
///     温层达到阈值自动晋升热层。
///
///   持久化：条目带 hits 与真实 LastUsed（修复旧版重启后 LRU 失真）；
///   key 前缀 kv3——3.0 一次性废弃 2.x 旧条目（旧格式文件读入即被忽略）。
/// </summary>
public sealed class CacheStage : IMessageStage, IDisposable
{
    /// <summary>温层条目命中达到该次数即晋升热层。</summary>
    public const int HotThreshold = 3;

    private sealed record CacheItem(string Translation, DateTime LastUsed, long Hits, bool Hot, int SourceLen);

    private readonly AppConfig _cfg;
    private readonly GlossaryStore _glossary;
    private readonly string _path;
    private readonly string _providerId;
    private readonly string _promptHash;

    private readonly ConcurrentDictionary<string, CacheItem> _entries = new();
    private readonly object _saveSync = new();
    private bool _dirty;
    private bool _disposed;

    /// <summary>温层容量预算：所有条目原文长度累计上限（字符）。测试可覆盖。</summary>
    public long WarmCapacityChars { get; set; } = 256 * 1024;

    public CacheStage(AppConfig cfg, GlossaryStore glossary, string path, string providerId)
    {
        _cfg = cfg;
        _glossary = glossary;
        _path = path;
        _providerId = providerId;
        _promptHash = Sha8(cfg.SystemPrompt);
        Load();
    }

    public string Id => "cache";
    public int Count => _entries.Count;
    public int HotCount => _entries.Values.Count(v => v.Hot);

    public Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct)
    {
        if (!_cfg.CacheEnabled) return Task.FromResult<PipelineResult?>(null);

        var key = BuildKey(ctx);
        ctx.CacheKey = key;

        if (_entries.TryGetValue(key, out var hit))
        {
            // 命中即计数；温层达到阈值晋升热层（热层命中只累加计数供观察）
            var hits = hit.Hits + 1;
            var hot = hit.Hot || hits >= HotThreshold;
            _entries[key] = hit with { LastUsed = DateTime.UtcNow, Hits = hits, Hot = hot };
            _dirty = true;
            return Task.FromResult<PipelineResult?>(new PipelineResult(
                TrafficKind.CacheHit, hot ? "C2" : "C", "缓存命中", hit.Translation));
        }
        return Task.FromResult<PipelineResult?>(null);
    }

    /// <summary>翻译成功后回写（存的是术语已还原的最终译文）。</summary>
    public void Put(TranslationContext ctx, string translation)
    {
        if (!_cfg.CacheEnabled || string.IsNullOrEmpty(ctx.CacheKey) ||
            string.IsNullOrEmpty(translation))
            return;

        // 已有条目（例如刚命中过）→ 保留命中计数；新条目 hits=1
        var prev = _entries.TryGetValue(ctx.CacheKey!, out var p) ? p : null;
        _entries[ctx.CacheKey!] = new CacheItem(
            translation, DateTime.UtcNow,
            (prev?.Hits ?? 0) + 1,
            prev?.Hot ?? false,
            prev?.SourceLen ?? (ctx.OriginalText?.Length ?? 0));
        EvictIfNeeded();
        _dirty = true;
    }

    public void Clear()
    {
        _entries.Clear();
        _dirty = true;
        SaveNow();
    }

    /// <summary>温层按累计原文长度预算淘汰（淘汰最旧 LastUsed 的 1/4）；热层不参与。</summary>
    private void EvictIfNeeded()
    {
        var warm = _entries.Where(kv => !kv.Value.Hot).ToList();
        long used = warm.Sum(x => (long)x.Value.SourceLen);
        if (used <= WarmCapacityChars || warm.Count == 0) return;

        var victims = warm.OrderBy(x => x.Value.LastUsed)
            .Take(Math.Max(1, warm.Count / 4)).ToList();
        foreach (var kv in victims)
            _entries.TryRemove(kv.Key, out _);
    }

    internal string BuildKey(TranslationContext ctx)
    {
        // kv3：3.0 双层缓存格式升级，2.x 旧条目（kv2/kv1）全部自然废弃，不清文件。
        var raw = string.Join('\x1f',
            "kv3",
            ctx.Text,
            ctx.TargetLang ?? ctx.Config.InTargetLang,
            _glossary.Version.ToString(),
            _providerId,
            _promptHash);
        return Sha8(raw);
    }

    private static string Sha8(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16];
    }

    // ---------- 持久化 ----------

    private sealed record Dto(long SavedAt, Dictionary<string, string[]> Entries);

    private static readonly JsonSerializerOptsHolder Opts = new();
    private sealed class JsonSerializerOptsHolder
    {
        public System.Text.Json.JsonSerializerOptions Value { get; } = new()
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dto = System.Text.Json.JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), Opts.Value);
            if (dto?.Entries == null) return;
            foreach (var kv in dto.Entries)
            {
                var v = kv.Value;
                if (v.Length < 2) continue;
                // v3 格式：[译文, LastUsed.Ticks, hits]；兼容 v2 旧格式 [译文, LastUsed]
                // （旧条目 hits=0 落温层；kv2 时代的 key 已不匹配，读到也只是无害残留）
                var hits = v.Length >= 3 && long.TryParse(v[2], out var h) ? h : 0;
                var lastUsed = long.TryParse(v[1], out var t) && t > 0
                    ? new DateTime(t) : DateTime.UtcNow;
                var srcLen = v.Length >= 4 && int.TryParse(v[3], out var sl) && sl > 0
                    ? sl : v[0].Length;   // 旧格式无原文长度，用译文长度近似
                _entries[kv.Key] = new CacheItem(v[0], lastUsed, hits, hits >= HotThreshold, srcLen);
            }
        }
        catch
        {
            // 缓存文件损坏按空缓存启动
        }
    }

    /// <summary>把内存缓存落盘（tmp + move 原子替换）。</summary>
    public void SaveNow()
    {
        if (!_dirty || _disposed) return;
        lock (_saveSync)
        {
            if (_disposed) return;
            try
            {
                var dto = new Dto(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    _entries.ToDictionary(
                        kv => kv.Key,
                        kv => new[] { kv.Value.Translation, kv.Value.LastUsed.Ticks.ToString(),
                                      kv.Value.Hits.ToString(), kv.Value.SourceLen.ToString() }));
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(dto, Opts.Value),
                    new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch
            {
                // 保存失败不中断服务，下次写入再试
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SaveNow();
    }
}
