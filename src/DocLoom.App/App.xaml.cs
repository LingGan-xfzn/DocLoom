using System.Windows;
using System.Windows.Threading;

namespace DocLoom.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 没有 StartupUri，窗口在这里显式创建：否则 Application 启动时零窗口会立即退出。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"出现了一个未预期的错误：{Environment.NewLine}{e.Exception.Message}",
            "DocLoom",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}