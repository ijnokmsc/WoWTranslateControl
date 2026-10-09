using System;

namespace WoWTranslateControl.Models;

/// <summary>
/// 可添加的远程翻译服务条目（v50 Provider 添加模式）。
/// 本地 llama 不在此列——它由 ActiveProviderId = "local" 特指。
/// </summary>
public sealed class ProviderEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名（服务控制下拉列表里展示）。</summary>
    public string Name { get; set; } = "";

    /// <summary>"openai" = OpenAI 兼容端点；"google" = 谷歌免费翻译（无需密钥）。</summary>
    public string Type { get; set; } = "openai";

    /// <summary>OpenAI 兼容端点完整 URL（含 /v1/chat/completions）。仅 Type=openai。</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>API 密钥（明文本机保存，随 settings.json）。仅 Type=openai。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>模型名。仅 Type=openai。</summary>
    public string Model { get; set; } = "";

    /// <summary>谷歌语言对。仅 Type=google。</summary>
    public string GoogleSl { get; set; } = "auto";
    public string GoogleTl { get; set; } = "zh-CN";

    /// <summary>类型显示名（列表/下拉用）。</summary>
    public string TypeLabel => Type == "google" ? "谷歌免费翻译" : "OpenAI 兼容";

    /// <summary>列表摘要（管理对话框用）：openai 显示模型名，google 显示语言对。</summary>
    public string Summary => Type == "google" ? $"{GoogleSl} → {GoogleTl}" : Model;

    // ---- 官方免费 API 预设（bilibili Index-Translate，向导「免费 API」与管理对话框共用） ----
    public const string FreeApiName = "Index-Translate 免费 API（B站官方）";
    public const string FreeApiEndpoint = "https://index-translate.bilibili.com/v1/chat/completions";
    public const string FreeApiKey = "index";
    public const string FreeApiModel = "Index-Translate-35B-A3B";

    public static ProviderEntry CreateFreeApi() => new()
    {
        Name = FreeApiName,
        Type = "openai",
        Endpoint = FreeApiEndpoint,
        ApiKey = FreeApiKey,
        Model = FreeApiModel,
    };
}
