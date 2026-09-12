using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWTranslateControl.Models;

/// <summary>
/// 持久化配置。保存为工程目录下的 settings.json，程序启动时读取。
/// </summary>
public sealed class AppConfig
{
    // ---------- llama-server ----------
    /// <summary>llama.cpp 目录。默认 = 运行目录\llama.cpp（便携化：新用户解压即用，向导也会自动生成此目录）。</summary>
    public string LlamaDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "llama.cpp");
    public string ModelFile { get; set; } = "Hy-MT2-1.8B-Q4_K_M.gguf";

    private int _threads;
    /// <summary>推理线程数。默认 = 逻辑核心数的一半（夹取 1..16），适配通用机器；老配置显式值优先。</summary>
    public int Threads
    {
        get => _threads > 0 ? _threads : Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
        set => _threads = value;
    }

    public int ContextSize { get; set; } = 2048;
    public int UpstreamPort { get; set; } = 8081;
    public bool AutoStartLlama { get; set; } = true;

    // ---------- 代理 ----------
    public int ListenPort { get; set; } = 8080;
    public bool ProxyAutoStart { get; set; } = true;

    /// <summary>
    /// 注入的翻译系统提示词。
    /// 插件的 WoWTranslate335.dll 不发 system 内容（请求模板里 system 为空串），
    /// 所以必须由代理补上，否则模型不知道这是翻译任务。
    /// 实测有效：126 组玩家聊天中 81 组成功翻译。
    /// </summary>
    public const string DefaultSystemPrompt =
        "You are a World of Warcraft translator. Translate the given game chat " +
        "from English (or mixed EN/ZH) into Chinese. Output ONLY the Chinese " +
        "translation, no explanation, no surrounding quotes. Preserve WoW " +
        "hyperlink tags (|H...|h...|h, |c...|r) and placeholders like " +
        "http://ph.wt/1 exactly as-is.";

    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    /// <summary>外发翻译（中文→英文）的系统提示词，与 DefaultSystemPrompt 成对。</summary>
    public const string DefaultSystemPromptZh2En =
        "You are a World of Warcraft translator. Translate the given game chat " +
        "from Chinese into English. Output ONLY the English translation, no explanation, " +
        "no surrounding quotes. Preserve WoW hyperlink tags (|H...|h...|h, |c...|r) and " +
        "placeholders like http://ph.wt/1 and ⟦G11⟧ exactly as-is.";

    /// <summary>
    /// 单次请求的 token 上限。DLL 不发 max_tokens，不设上限时模型可能一直生成到耗尽上下文。
    /// 翻译结果通常很短，实测 128 足够，这里放宽到 256 留余量。
    /// </summary>
    public int MaxTokensCeil { get; set; } = 256;

    /// <summary>转发到 llama-server 的超时（秒）。</summary>
    public int UpstreamTimeoutSec { get; set; } = 60;

    /// <summary>是否启用响应缓存。日志显示请求重复率达 3.9 倍，缓存可直接吃掉大部分负载。</summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>响应缓存条目上限，超出后按最久未使用淘汰。</summary>
    public int CacheMaxEntries { get; set; } = 2000;

    /// <summary>
    /// 缓存是否持久化到 cache.json（重启不丢）。
    /// 注意：持久化缓存与插件端 WoWTranslateCache 是两层独立缓存，
    /// 术语表变更由版本号机制自动失效本层；插件层需 /wt clearcache 手动清（ADR-003）。
    /// </summary>
    public bool CachePersist { get; set; } = true;

    // ---------- 术语表 ----------

    /// <summary>是否启用本地术语表（⟦Gn⟧ 占位符保护替换）。</summary>
    public bool GlossaryEnabled { get; set; } = true;

    /// <summary>目标语言代码（参与缓存 key；插件侧incomingToLang 一般为 zh）。</summary>
    public string InTargetLang { get; set; } = "zh";

    // ---------- 过滤规则开关 ----------
    /// <summary>R1 团队图标标记刷屏。日志占比 27.1%，重复放大 18.4 倍，单项最大元凶。</summary>
    public bool RuleIconSpam { get; set; } = true;

    /// <summary>R2 SPELL_* 战斗日志。日志占比 50.6%。</summary>
    public bool RuleSpellLog { get; set; } = true;

    /// <summary>R3 SWING_/RANGE_/ENVIRONMENTAL_ 等其他战斗事件。</summary>
    public bool RuleCombatOther { get; set; } = true;

    /// <summary>R4 拾取/分配/制造播报（获得了物品、赢得了、贪婪、制造了…）。日志占比 10.7%。</summary>
    public bool RuleLoot { get; set; } = true;

    /// <summary>R5 伤害/死亡/表情播报（点物理伤害、杀死了、挥了挥手…）。</summary>
    public bool RuleDamageDeath { get; set; } = true;

    /// <summary>
    /// R6 中文兜底：剥掉魔兽标记后，中文字符数 >= 1 且英文字母数 &lt;= 中文数 * 倍率，
    /// 判定为"本来就是中文、无需 EN→ZH 翻译"。
    /// 这是启发式规则，可能误伤极短的中英混合消息，可在界面上单独关闭。
    /// </summary>
    public bool RuleChineseOnly { get; set; } = true;

    /// <summary>R6 的英文/中文倍率阈值。越小越激进（过滤越多）。</summary>
    public double ChineseRatioLimit { get; set; } = 2.0;

    /// <summary>
    /// R7 乱码兜底：最大连续段 4+ 个 '?' 判定为编码损坏文本（GS DLL 把中文逐字转成 ? 的产物），
    /// 翻译它毫无意义，直接拦截。真实玩家聊天分散 3 连问号（"what??? really???"）不误杀，可单独关闭。
    /// </summary>
    public bool RuleMangled { get; set; } = true;

    /// <summary>
    /// R8 已含中文：剥离魔兽标记后中文字数 ≥ 阈值即拦截，不看英文比例。
    /// 针对 Questie/DBM 等插件播报——内容本就是中文（addon 名、GitHub/Discord 链接
    /// 拉高英文字母数，R6 比例规则擦边漏过），模型只会把中文翻成另一种中文，纯浪费。
    /// </summary>
    public bool RuleChinesePresent { get; set; } = true;

    /// <summary>R8 的中文字数阈值。</summary>
    public int ChinesePresentMinChars { get; set; } = 8;

    // ---------- 频道过滤（C0-C8） ----------
    /// <summary>频道过滤总开关。频道标签由插件 Lua 补丁注入（\1CH\1 前缀），控制台无条件剥离。</summary>
    public bool ChannelFilterEnabled { get; set; } = true;

    /// <summary>C1 综合（SAY）。false = 该频道消息不进模型，直接回显原文。</summary>
    public bool ChannelSay { get; set; } = true;
    /// <summary>C2 喊话（YELL）。</summary>
    public bool ChannelYell { get; set; } = true;
    /// <summary>C3 密语（WHISPER）。</summary>
    public bool ChannelWhisper { get; set; } = true;
    /// <summary>C4 队伍（PARTY）。</summary>
    public bool ChannelParty { get; set; } = true;
    /// <summary>C5 公会（GUILD/OFFICER）。</summary>
    public bool ChannelGuild { get; set; } = true;
    /// <summary>C6 团队（RAID/RAID_LEADER/RAID_WARNING）。</summary>
    public bool ChannelRaid { get; set; } = true;
    /// <summary>C7 战场（BATTLEGROUND）。</summary>
    public bool ChannelBattleground { get; set; } = true;
    /// <summary>C8 世界/本地（CHANNEL 自定义频道）。</summary>
    public bool ChannelWorld { get; set; } = true;
    /// <summary>C0 未标记（旧版 Lua 未打频道标签，或外发翻译请求）。</summary>
    public bool ChannelUntagged { get; set; } = true;

    // ---------- 翻译服务（Provider） ----------
    /// <summary>Provider 模式：local=本地 llama | openai=OpenAI 兼容 | google=谷歌免费 | auto=故障转移链 llama→openai→google。</summary>
    public string ProviderMode { get; set; } = "local";

    /// <summary>OpenAI 兼容端点完整 URL（含 /v1/chat/completions）。</summary>
    public string OpenAiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";

    /// <summary>谷歌免费翻译源/目标语言（client=gtx 公开端点，无需密钥）。</summary>
    public string GoogleSl { get; set; } = "auto";
    public string GoogleTl { get; set; } = "zh-CN";

    // ---------- 插件一键配置 ----------
    /// <summary>游戏客户端根目录（含 Wow.exe / Interface / WTF）。</summary>
    public string GameDir { get; set; } = @"D:\Games\TriumvirateWoW";

    /// <summary>频道说明目录（供界面展示与一键配置同步插件 incomingChannels）。</summary>
    [JsonIgnore]
    public static readonly (string RuleId, string WowName, string CnName, string CfgKey)[] ChannelCatalog =
    {
        ("C1", "SAY",         "综合",      "ChannelSay"),
        ("C2", "YELL",        "喊话",      "ChannelYell"),
        ("C3", "WHISPER",     "密语",      "ChannelWhisper"),
        ("C4", "PARTY",       "队伍",      "ChannelParty"),
        ("C5", "GUILD",       "公会",      "ChannelGuild"),
        ("C6", "RAID",        "团队",      "ChannelRaid"),
        ("C7", "BATTLEGROUND","战场",      "ChannelBattleground"),
        ("C8", "CHANNEL",     "世界/本地", "ChannelWorld"),
        ("C0", "—",           "未标记",    "ChannelUntagged"),
    };

    // ---------- 界面 ----------
    /// <summary>流量列表最多保留多少行，防止长时间挂机后 UI 卡顿或内存增长。</summary>
    public int LogMaxRows { get; set; } = 400;

    /// <summary>实时流量列表只显示带频道标签的聊天消息（隐藏战斗/拾取/伤亡等系统播报）。</summary>
    public bool TrafficOnlyChannel { get; set; } = false;

    /// <summary>是否把过滤与转发明细写入文件日志。</summary>
    public bool WriteFileLog { get; set; } = false;   // 发布版默认关（避免玩家目录写日志），排错时在界面勾选打开

    /// <summary>最小化/关闭时缩到系统托盘（双击托盘还原，托盘菜单可真正退出）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    // ---------- 插件 DLL 轨道（ADR-007 双轨并行） ----------
    /// <summary>当前偏好轨道："gs" = Track A 现役 GS 插件；"direct" = Track B 自有 Direct DLL（实验）。</summary>
    public string PluginTrack { get; set; } = "gs";

    // ---------- Track B Direct DLL 显示（v16 全自治驱动） ----------
    /// <summary>
    /// Track B 驱动 Lua 的译文显示模式："replace" = 译文替换原文（失败/超时回显原文）；
    /// "both" = 原文照常显示，译文加前缀另起一行。随 WoWTranslateDirect.json 写入游戏目录，重登生效。
    /// </summary>
    public string DirectDisplayMode { get; set; } = "replace";

    /// <summary>Track B both 模式的译文行前缀。</summary>
    public string DirectDisplayPrefix { get; set; } = "[译]";

    /// <summary>
    /// Track B 外发翻译模式："off" = 关闭；"replace" = 你发的中文只发英文译文（失败发原文）；
    /// "both" = 原文和英文都发。随 WoWTranslateDirect.json 写入游戏目录，重登生效。
    /// </summary>
    public string DirectOutgoingMode { get; set; } = "replace";

    // ---------- 过滤规则说明（只读，供界面展示） ----------
    [JsonIgnore]
    public static readonly (string Key, string Name, string Detail)[] RuleCatalog =
    {
        ("RuleIconSpam", "R1 图标刷屏",
         "含 RaidTargetingIcon 或以 |Hicon: 开头。团队标记图标+玩家名，无自然语言。"),
        ("RuleSpellLog", "R2 战斗日志",
         "含 SPELL_AURA_APPLIED / SPELL_AURA_REMOVED / SPELL_PERIODIC_HEAL 等事件标识。"),
        ("RuleCombatOther", "R3 其他战斗事件",
         "含 SWING_ / RANGE_ / ENVIRONMENTAL_ 前缀的战斗事件标识。"),
        ("RuleLoot", "R4 拾取播报",
         "含 获得了物品 / 赢得了 / 放弃了 / 贪婪 / 需求 / 掷点 等中文固定词，及 automatically passes on / cannot loot that item / wins the roll 等英文固定句式。"),
        ("RuleDamageDeath", "R5 伤害死亡播报",
         "含 点物理伤害 / 吸收了 / 杀死了 / 死亡 / 挥了挥手 等中文播报。"),
        ("RuleChineseOnly", "R6 中文兜底（启发式）",
         "剥离魔兽标记后中文占主导，判定无需 EN→ZH 翻译。可单独关闭以防误伤。"),
        ("RuleMangled", "R7 乱码兜底",
         "最大连续段 4+ 个 '?' 判定为编码损坏文本（GS DLL 出入站把中文逐字转成 ? 的产物），翻译无意义，直接拦截。分散 3 连问号的真实聊天不误杀。"),
        ("RuleChinesePresent", "R8 已含中文（插件播报）",
         "剥离标记后中文 ≥8 字即拦截，不看英文比例。针对 Questie/DBM 等中英混合插件播报（R6 比例规则漏过），避免中文→中文空转。"),
    };

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时回落到默认值，不能让程序起不来
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 保存失败不应中断服务运行
        }
    }
}
