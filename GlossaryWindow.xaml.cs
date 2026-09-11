using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using WoWTranslateControl.Core.Glossary;

namespace WoWTranslateControl;

/// <summary>术语表管理窗口：增删改查 + CSV 导入导出。修改即时落盘并 bump 版本。</summary>
public sealed class GlossaryRow
{
    public int Id { get; init; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public partial class GlossaryWindow : Window
{
    private readonly GlossaryStore _store;
    private readonly ObservableCollection<GlossaryRow> _rows = new();

    public GlossaryWindow(GlossaryStore store)
    {
        InitializeComponent();
        _store = store;
        Grid.ItemsSource = _rows;
        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var e in _store.Snapshot())
            _rows.Add(new GlossaryRow { Id = e.Id, From = e.From, To = e.To });
        StatusText.Text = $"共 {_rows.Count} 条（版本 {_store.Version}）";
    }

    private GlossaryRow? Selected => Grid.SelectedItem as GlossaryRow;

    private void ShowError(string title, Exception ex)
        => MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.Add(TxtFrom.Text, TxtTo.Text);
            TxtFrom.Clear();
            TxtTo.Clear();
            Reload();
        }
        catch (Exception ex) { ShowError("添加失败", ex); }
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { StatusText.Text = "请先选中一行"; return; }
        try
        {
            _store.Update(Selected.Id, Selected.From, Selected.To);
            Reload();
        }
        catch (Exception ex) { ShowError("更新失败", ex); }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) { StatusText.Text = "请先选中一行"; return; }
        _store.Remove(Selected.Id);
        Reload();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Count == 0) return;
        var r = MessageBox.Show(this, $"确定清空全部 {_store.Count} 条术语？",
            "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _store.Clear();
        Reload();
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|全部文件 (*.*)|*.*",
            Title = "导入术语表",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (added, skipped) = _store.ImportCsv(dlg.FileName);
            Reload();
            StatusText.Text = $"导入完成：新增 {added}，跳过 {skipped}";
        }
        catch (Exception ex) { ShowError("导入失败", ex); }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = "glossary.csv",
            Title = "导出术语表",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _store.ExportCsv(dlg.FileName);
            StatusText.Text = $"已导出 {_store.Count} 条";
        }
        catch (Exception ex) { ShowError("导出失败", ex); }
    }
}
