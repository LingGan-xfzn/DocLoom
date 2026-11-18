using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DocLoom.App.Services;
using DocLoom.App.ViewModels;
using DocLoom.Core;

namespace DocLoom.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowEffects.Apply(this);
        PlayWindowIntro();

        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count > 0)
        {
            await _viewModel.LoadFilesAsync(files);
        }
    }

    /// <summary>文档切换：标题卡、右栏、段落与图片依次入场。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Current) || _viewModel.Current is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PlayDocumentIntro));
    }

    private void PlayDocumentIntro()
    {
        StaggerAnimation.FadeRise(TitleCard, 0.45, 10);
        StaggerAnimation.FadeRise(RightPanel, 0.5, 8, 60);
        StaggerAnimation.Play(ParagraphList, maxItems: 12, stepMs: 26, riseDistance: 10);
        StaggerAnimation.Play(ImageList, maxItems: 12, stepMs: 24, riseDistance: 8);
    }

    /// <summary>窗口首次出现：轻微上浮 + 提亮，避免"啪"地弹出来。</summary>
    private void PlayWindowIntro()
    {
        if (RootBorder.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
        {
            transform = new TranslateTransform(0, 0);
            RootBorder.RenderTransform = transform;
        }

        RootBorder.Opacity = 1;
        RootBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.62, 1, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // WindowChrome 在最大化时会把内容顶到屏幕边缘，这里补回一圈边框厚度。
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(7);
            MaxIcon.Data = (Geometry)FindResource("IconRestore");
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            MaxIcon.Data = (Geometry)FindResource("IconMaximize");
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _viewModel.OpenFileCommand.Execute(null);
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            if (_viewModel.Current is not null)
            {
                _viewModel.Current.CopyAllCommand.Execute(null);
            }

            return;
        }

        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _viewModel.ExportCommand.Execute(null);
            return;
        }

        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control && _viewModel.Current is not null)
        {
            e.Handled = true;
            _viewModel.BrowseFolderCommand.Execute(null);
        }
        else if (e.Key == Key.Escape && _viewModel.IsToastVisible)
        {
            _viewModel.IsToastVisible = false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 拖动过程中窗口被关闭时会抛，忽略即可。
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------- 拖放 ----------

    private void Window_DragEnter(object sender, DragEventArgs e) => HandleDragOver(e);

    private void Window_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);

    private void HandleDragOver(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            _viewModel.DragActive = false;
            e.Handled = true;
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        var supported = files.Any(f => Directory.Exists(f) || DocumentParser.IsSupported(f));
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.DragActive = supported;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _viewModel.DragActive = false;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        _viewModel.DragActive = false;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        var expanded = new List<string>();
        foreach (var path in files)
        {
            if (Directory.Exists(path))
            {
                expanded.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(DocumentParser.IsSupported));
            }
            else
            {
                expanded.Add(path);
            }
        }

        if (expanded.Count > 0)
        {
            await _viewModel.LoadFilesAsync(expanded);
        }
    }
}