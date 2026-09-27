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

    /// <summary>
    /// llama-server 工作集内存阈值（MB），持续超限约 60 秒自动重启回收。
    /// 1.8B Q4 模型基线约 1.5-2 GB，长跑增长（碎片/KV 缓存）超 4 GB 即视为泄漏。
    /// </summary>
    public int LlamaMemLimitMB { get; set; } = 4096;

    /// <summary>总开关：内存超限自动重启 + 崩溃后自动拉起 llama-server。</summary>
    public bool LlamaAutoRestart { get; set; } = true;

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

    /// <summary>
    /// 外发翻译（中文→英文）的系统提示词。采用 Hy-MT 系列翻译模型的官方训练格式
    /// （中文指令），英文指令会导致该模型偶发输出中文改写而非英文译文。
    /// </summary>
    public const string DefaultSystemPromptZh2En =
        "把下面的文本翻译成英文，不要额外解释。";

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





    /// <summary>
    /// R7 乱码兜底：最大连续段 4+ 个 '?' 判定为编码损坏文本（GS DLL 把中文逐字转成 ? 的产物），
    /// 翻译它毫无意义，直接拦截。真实玩家聊天分散 3 连问号（"what??? really???"）不误杀，可单独关闭。
    /// </summary>
    public bool RuleMangled { get; set; } = true;

    /// <summary>
    /// R8 已含中文：剥离魔兽标记后中文字数 ≥ 阈值即拦截，不看英文比例。
    /// 针对 Questie/DBM 等插件播报——内容本就是中文（addon 名、GitHub/Discord 链接
    /// 拉高英文字母数擦边漏过），模型只会把中文翻成另一种中文，纯浪费。
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

    // ---------- 游戏目录与部署 ----------
    /// <summary>游戏客户端根目录（含 Wow.exe / Interface / WTF）。</summary>
    public string GameDir { get; set; } = @"D:\Games\TriumvirateWoW";

    /// <summary>频道说明目录（供界面展示）。</summary>
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

    // ---------- Track B Direct DLL 显示（v16 全自治驱动） ----------
    /// <summary>
    /// Track B 驱动 Lua 的译文显示模式："replace" = 译文替换原文（失败/超时回显原文）；
    /// "both" = 原文照常显示，译文加前缀另起一行。随 WoWTranslateDirect.json 写入游戏目录，重登生效。
    /// </summary>
    public string DirectDisplayMode { get; set; } = "replace";

    /// <summary>Track B both 模式的译文行前缀。</summary>
    public string DirectDisplayPrefix { get; set; } = "[译]";

    /// <summary>游戏目录 DLL 文件日志（WoWTranslateDirect.log）开关。默认关，排错时打开。</summary>
    public bool DllLogEnabled { get; set; } = false;

    /// <summary>
    /// Track B 外发翻译模式："off" = 关闭；"replace" = 你发的中文只发英文译文（失败发原文）；
    /// "both" = 原文和英文都发。随 WoWTranslateDirect.json 写入游戏目录，重登生效。
    /// </summary>
    public string DirectOutgoingMode { get; set; } = "replace";

    /// <summary>
    /// v41 发送消息过滤器：免翻译前缀规则，每行一条，消息以其开头则原样发送不翻译。
    /// 另有内置恒生效规则：首字符 . 或 。（私服命令）在驱动 Lua 里硬编码，不在此列。
    /// 随 WoWTranslateDirect.json 的 outFilter 键同步到 DLL。
    /// </summary>
    public string OutgoingFilterRules { get; set; } = ".\n。";

    /// <summary>
    /// v41 外发频道开关：不翻译的频道列表（逗号分隔的大写频道名，如 "WHISPER,CHANNEL"），
    /// 空 = 全部频道都翻译。随 WoWTranslateDirect.json 的 outOff 键同步到 DLL。
    /// </summary>
    public string DirectOutgoingOffChannels { get; set; } = "";

    /// <summary>用户点了「卸载 Direct DLL」后置位：阻止启动/改目录时的自动部署悄悄重装。点「重新部署」时清除。</summary>
    public bool SkipAutoDeploy { get; set; } = false;

    // ---------- 内部质检规则说明（只读；R1-R6 用户内容规则已随 GS 插件链路移除） ----------
    [JsonIgnore]
    public static readonly (string Key, string Name, string Detail)[] RuleCatalog =
    {
        ("RuleMangled", "R7 乱码兜底",
         "最大连续段 4+ 个 '?' 判定为编码损坏文本（GS DLL 出入站把中文逐字转成 ? 的产物），翻译无意义，直接拦截。分散 3 连问号的真实聊天不误杀。"),
        ("RuleChinesePresent", "R8 已含中文（插件播报）",
         "剥离标记后中文 ≥8 字即拦截，不看英文比例。针对 Questie/DBM 等中英混合插件播报），避免中文→中文空转。"),
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
