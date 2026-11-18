using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DocLoom.App;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DocLoom",
        "error.log");

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
        // 记录到日志并在界面内提示，不弹模态框打断操作。
        WriteLog(e.Exception);

        if (MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.ShowToast($"操作未完成：{e.Exception.Message}", ViewModels.ToastKind.Error);
        }

        e.Handled = true;
    }

    private static void WriteLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}