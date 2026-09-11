// 启用 WinForms（托盘 NotifyIcon）后与 WPF 产生一批同名类型，全局消歧到 WPF 语义。
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using Application = System.Windows.Application;
