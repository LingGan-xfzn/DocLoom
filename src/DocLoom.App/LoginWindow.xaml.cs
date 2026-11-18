using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DocLoom.App.Services;
using DocLoom.Core.Xiumi;
using Microsoft.Web.WebView2.Core;

namespace DocLoom.App;

public partial class LoginWindow : Window
{
    private const string LoginUrl = "https://xiumi.us/studio/v5";

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _statusTimer;
    private bool _checking;
    private bool _completed;

    public LoginWindow()
    {
        InitializeComponent();

        // 多显示器下 WPF 的 CenterScreen 会把窗口算到副屏甚至屏幕外，
        // 这里按系统工作区显式居中。
        var area = SystemParameters.WorkArea;
        Width = Math.Min(940, Math.Max(680, area.Width - 80));
        Height = Math.Min(720, Math.Max(520, area.Height - 80));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        _pollTimer.Tick += async (_, _) => await TryDetectLoginAsync();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.2) };
        _statusTimer.Tick += (_, _) => UpdateWaitingStatus();

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _statusTimer.Stop();
        };
    }

    public string? CookieHeader { get; private set; }

    public XiumiUser? User { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowEffects.Apply(this);

            // 用户数据目录固定到 LocalAppData：程序可能装在只读目录，且登录态可以留在这里。
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocLoom",
                "WebView2");
            Directory.CreateDirectory(dataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += async (_, _) => await TryDetectLoginAsync();
            Browser.Source = new Uri(LoginUrl);
            LoadingLayer.Visibility = Visibility.Collapsed;
            _pollTimer.Start();
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            LoadingLayer.Visibility = Visibility.Visible;
            SetStatus($"无法启动内置浏览器：{ex.Message}", isError: true);
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private async Task TryDetectLoginAsync()
    {
        if (_checking || _completed || Browser.CoreWebView2 is null)
        {
            return;
        }

        _checking = true;
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://xiumi.us");
            if (cookies.Count == 0)
            {
                return;
            }

            var header = string.Join("; ", cookies
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Select(c => $"{c.Name}={c.Value}"));

            using var client = new XiumiClient(header);
            var user = await client.GetMeAsync();
            if (string.IsNullOrWhiteSpace(user.UniqueUid))
            {
                return;
            }

            CookieHeader = header;
            User = user;
            _completed = true;
            _pollTimer.Stop();
            _statusTimer.Stop();
            SetStatus($"已登录：{user.Nickname ?? user.UniqueUid}", isError: false, ok: true);
            DialogResult = true;
            Close();
        }
        catch (XiumiException)
        {
            // 还没登录，继续等。
        }
        catch (Exception)
        {
            // 网络抖动等，下一轮再试。
        }
        finally
        {
            _checking = false;
        }
    }

    private void UpdateWaitingStatus()
    {
        if (_completed)
        {
            return;
        }

        SetStatus("等待登录：在页面里登录后会自动完成", isError: false);
    }

    private void SetStatus(string text, bool isError, bool ok = false)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(isError ? "DangerBrush" : ok ? "SuccessBrush" : "WarningBrush");
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        Browser.Source = new Uri(LoginUrl);
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            DialogResult = true;
            Close();
            return;
        }

        await TryDetectLoginAsync();
        if (!_completed)
        {
            SetStatus("还检测不到登录状态，请先在页面里完成登录", isError: true);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}