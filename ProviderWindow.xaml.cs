using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WoWTranslateControl.Models;

namespace WoWTranslateControl;

/// <summary>
/// 翻译服务管理对话框（v50 Provider 添加模式）：增删改 Providers 列表。
/// 条目直接编辑 AppConfig.Providers 内存对象，「保存并关闭」后由调用方重新加载下拉。
/// </summary>
public partial class ProviderWindow : Window
{
    private readonly AppConfig _cfg;

    /// <summary>列表内容是否有改动（调用方据此重新加载下拉）。</summary>
    public bool Saved { get; private set; }

    public ProviderWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        ReloadList();
        if (LstProviders.Items.Count > 0)
            LstProviders.SelectedIndex = 0;
        UpdateFormVisibility();
    }

    private void ReloadList()
    {
        LstProviders.ItemsSource = null;
        LstProviders.ItemsSource = _cfg.Providers;
    }

    private ProviderEntry? Selected => LstProviders.SelectedItem as ProviderEntry;

    private void LstProviders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => LoadEntryToForm();

    private void LoadEntryToForm()
    {
        var p = Selected;
        TxtEditingName.Text = p?.Name ?? "（未选中）";
        if (p == null)
        {
            TxtName.Text = TxtEndpoint.Text = TxtApiKey.Text = TxtModel.Text = "";
            return;
        }
        TxtName.Text = p.Name;
        SelectType(p.Type);
        TxtEndpoint.Text = p.Endpoint;
        TxtApiKey.Text = p.ApiKey;
        TxtModel.Text = p.Model;
        TxtGoogleSl.Text = p.GoogleSl;
        TxtGoogleTl.Text = p.GoogleTl;
        UpdateFormVisibility();
    }

    private void SelectType(string type)
    {
        foreach (ComboBoxItem item in CmbType.Items)
            if ((string)item.Tag == type) { CmbType.SelectedItem = item; return; }
        CmbType.SelectedIndex = 0;
    }

    private string SelectedType =>
        CmbType.SelectedItem is ComboBoxItem item ? (string)item.Tag : "openai";

    private void CmbType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormVisibility();
        // 切类型是编辑中状态：提示尚未保存，避免误以为已生效
        if (Selected != null && IsLoaded)
            TxtHint.Text = "类型已切换（未保存）——核对字段后点「保存修改」";
    }

    private void UpdateFormVisibility()
    {
        var google = SelectedType == "google";
        PnlOpenAi.Visibility = google ? Visibility.Collapsed : Visibility.Visible;
        PnlGoogle.Visibility = google ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var entry = new ProviderEntry { Name = "新翻译服务", Type = "openai" };
        _cfg.Providers.Add(entry);
        Saved = true;
        ReloadList();
        LstProviders.SelectedItem = entry;
        TxtName.SelectAll();
        TxtName.Focus();
        TxtHint.Text = "已添加，填好表单后点「保存修改」";
    }

    private void BtnAddFreeApi_Click(object sender, RoutedEventArgs e)
    {
        var existing = _cfg.Providers.FirstOrDefault(p =>
            string.Equals(p.Endpoint, ProviderEntry.FreeApiEndpoint, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            LstProviders.SelectedItem = existing;
            TxtHint.Text = "免费 API 已在列表中（OpenAI 兼容类型，非谷歌翻译）";
            return;
        }
        var entry = ProviderEntry.CreateFreeApi();
        _cfg.Providers.Add(entry);
        Saved = true;
        ReloadList();
        LstProviders.SelectedItem = entry;
        TxtHint.Text = "免费 API 已添加（OpenAI 兼容类型）";
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null) return;
        if (MessageBox.Show(this, $"删除服务「{p.Name}」？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _cfg.Providers.Remove(p);
        Saved = true;
        ReloadList();
        LoadEntryToForm();
        TxtHint.Text = "已删除";
    }

    private void BtnSaveEntry_Click(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null)
        {
            MessageBox.Show(this, "请先在上方列表选中一个服务（或点「添加自定义…」）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = TxtName.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var type = SelectedType;
        if (type == "openai" && !TxtEndpoint.Text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "接口地址必须是完整 URL（含 /v1/chat/completions）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        p.Name = name;
        p.Type = type;
        p.Endpoint = TxtEndpoint.Text.Trim();
        p.ApiKey = TxtApiKey.Text.Trim();
        p.Model = TxtModel.Text.Trim();
        p.GoogleSl = string.IsNullOrWhiteSpace(TxtGoogleSl.Text) ? "auto" : TxtGoogleSl.Text.Trim();
        p.GoogleTl = string.IsNullOrWhiteSpace(TxtGoogleTl.Text) ? "zh-CN" : TxtGoogleTl.Text.Trim();
        Saved = true;
        ReloadList();
        LstProviders.SelectedItem = p;
        TxtEditingName.Text = p.Name;
        TxtHint.Text = "已保存";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
