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
/// 响应缓存阶段。
///
/// v2 关键变化（ADR-003）：
///   cache key = SHA256(原文 | 目标语 | 术语表版本 | ProviderId | systemPrompt哈希)
///   —— 术语表任何增删改都会 bump Version，旧译文自然失效，不存在"改了没效果"。
///   缓存持久化到 cache.json，控制台重启不丢；写入用 tmp+move 原子替换。
/// </summary>
public sealed class CacheStage : IMessageStage, IDisposable
{
    private sealed record CacheItem(string Translation, DateTime LastUsed);

    private readonly AppConfig _cfg;
    private readonly GlossaryStore _glossary;
    private readonly string _path;
    private readonly string _providerId;
    private readonly string _promptHash;

    private readonly ConcurrentDictionary<string, CacheItem> _entries = new();
    private readonly object _saveSync = new();
    private bool _dirty;
    private bool _disposed;

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

    public Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct)
    {
        if (!_cfg.CacheEnabled) return Task.FromResult<PipelineResult?>(null);

        var key = BuildKey(ctx);
        ctx.CacheKey = key;

        if (_entries.TryGetValue(key, out var hit))
        {
            _entries[key] = hit with { LastUsed = DateTime.UtcNow };
            return Task.FromResult<PipelineResult?>(new PipelineResult(
                TrafficKind.CacheHit, "C", "缓存命中", hit.Translation));
        }
        return Task.FromResult<PipelineResult?>(null);
    }

    /// <summary>翻译成功后回写（存的是术语已还原的最终译文）。</summary>
    public void Put(TranslationContext ctx, string translation)
    {
        if (!_cfg.CacheEnabled || string.IsNullOrEmpty(ctx.CacheKey) ||
            string.IsNullOrEmpty(translation))
            return;

        _entries[ctx.CacheKey!] = new CacheItem(translation, DateTime.UtcNow);
        EvictIfNeeded();
        _dirty = true;
    }

    public void Clear()
    {
        _entries.Clear();
        _dirty = true;
        SaveNow();
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count <= _cfg.CacheMaxEntries) return;
        foreach (var kv in _entries.OrderBy(x => x.Value.LastUsed)
                     .Take(_entries.Count / 4).ToList())
            _entries.TryRemove(kv.Key, out _);
    }

    internal string BuildKey(TranslationContext ctx)
    {
        // kv2：v22 前频道标签曾泄漏进模型（译文带 ?频道? 幻觉前缀）并被缓存，key 加版本
        // 号让历史脏条目全部自然失效，不清文件。
        var raw = string.Join('\x1f',
            "kv2",
            ctx.Text,
            ctx.Config.InTargetLang,
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
                _entries[kv.Key] = new CacheItem(kv.Value[0], DateTime.UtcNow);
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
                        kv => new[] { kv.Value.Translation, kv.Value.LastUsed.Ticks.ToString() }));
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
