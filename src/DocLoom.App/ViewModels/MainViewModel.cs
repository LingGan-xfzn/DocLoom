using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using DocLoom.App.Services;
using DocLoom.Core;
using DocLoom.Core.Export;
using Microsoft.Win32;

namespace DocLoom.App.ViewModels;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>主窗口的视图模型：文件载入、图片导出、提示与进度。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _toastTimer;

    private DocumentItemViewModel? _current;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private double _progressPercent;
    private string _targetFolder = string.Empty;
    private string _lastExportFolder = string.Empty;
    private bool _hasExportResult;
    private bool _isToastVisible;
    private string _toastText = string.Empty;
    private ToastKind _toastKind = ToastKind.Info;
    private string _errorMessage = string.Empty;
    private bool _dragActive;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _toastTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3.6),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => HasDocument && SelectedImageCount > 0 && !IsBusy);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(LastExportFolder));
        SelectRecentCommand = new RelayCommand(parameter =>
        {
            if (parameter is DocumentItemViewModel document)
            {
                Current = document;
            }
        });
        ClearRecentCommand = new RelayCommand(() =>
        {
            RecentDocuments.Clear();
            Current = null;
            ShowToast("已清空本次会话的文档");
        });
        ResetFolderCommand = new RelayCommand(() =>
        {
            if (Current is not null)
            {
                TargetFolder = ImageExporter.DefaultTargetFolder(Current.Model);
            }
        });
    }

    public ObservableCollection<DocumentItemViewModel> RecentDocuments { get; } = new();

    public DocumentItemViewModel? Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(HasNoDocument));
            OnPropertyChanged(nameof(SelectedImageCount));
            if (value is not null)
            {
                TargetFolder = ImageExporter.DefaultTargetFolder(value.Model);
                HasExportResult = false;
                LastExportFolder = string.Empty;
                _ = value.LoadThumbnailsAsync();
            }

            UpdateCommandStates();
        }
    }

    public bool HasDocument => Current is not null;

    public bool HasNoDocument => Current is null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string TargetFolder
    {
        get => _targetFolder;
        private set
        {
            if (SetProperty(ref _targetFolder, value))
            {
                OnPropertyChanged(nameof(TargetFolderHint));
            }
        }
    }

    public string TargetFolderHint => string.IsNullOrWhiteSpace(TargetFolder)
        ? "默认：桌面 / 文档同名文件夹"
        : TargetFolder;

    public string LastExportFolder
    {
        get => _lastExportFolder;
        private set
        {
            if (SetProperty(ref _lastExportFolder, value))
            {
                OpenFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasExportResult
    {
        get => _hasExportResult;
        private set => SetProperty(ref _hasExportResult, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        set => SetProperty(ref _isToastVisible, value);
    }

    public string ToastText
    {
        get => _toastText;
        private set => SetProperty(ref _toastText, value);
    }

    public ToastKind ToastKind
    {
        get => _toastKind;
        private set => SetProperty(ref _toastKind, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool DragActive
    {
        get => _dragActive;
        set => SetProperty(ref _dragActive, value);
    }

    public int SelectedImageCount => Current?.SelectedCount ?? 0;

    public AsyncRelayCommand OpenFileCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public RelayCommand BrowseFolderCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand SelectRecentCommand { get; }

    public RelayCommand ClearRecentCommand { get; }

    public RelayCommand ResetFolderCommand { get; }

    public async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        var files = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var supported = files.Where(DocumentParser.IsSupported).ToList();
        if (supported.Count == 0)
        {
            ShowToast("请拖入 .doc 或 .docx 文件", ToastKind.Warning);
            return;
        }

        if (supported.Count < files.Count)
        {
            ShowToast("已忽略不支持的文件，仅处理 Word 文档", ToastKind.Warning);
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        BusyText = "正在解析文档…";
        ProgressPercent = 0;

        DocumentItemViewModel? first = null;
        var failures = new List<string>();

        try
        {
            foreach (var path in supported)
            {
                try
                {
                    var parsed = await Task.Run(() => DocumentParser.Parse(path)).ConfigureAwait(true);
                    var viewModel = new DocumentItemViewModel(
                        parsed,
                        message => ShowToast(message, ToastKind.Success),
                        OnImageSelectionChanged);

                    var existing = RecentDocuments.FirstOrDefault(d =>
                        string.Equals(d.FilePath, viewModel.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        RecentDocuments.Remove(existing);
                    }

                    RecentDocuments.Insert(0, viewModel);
                    first ??= viewModel;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
                }
            }

            if (first is null)
            {
                ErrorMessage = failures.Count > 0 ? string.Join(Environment.NewLine, failures) : "文档解析失败";
                ShowToast("文档解析失败，请查看提示", ToastKind.Error);
                return;
            }

            Current = first;
            if (failures.Count > 0)
            {
                ShowToast($"部分文件失败：{failures[0]}", ToastKind.Warning);
            }
            else
            {
                ShowToast($"已解析 {supported.Count} 个文档", ToastKind.Success);
            }
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
            UpdateCommandStates();
        }
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ShowToast(message, kind));
            return;
        }

        ToastText = message;
        ToastKind = kind;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Word 文档",
            Filter = DocumentParser.BuildFilterString(),
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadFilesAsync(dialog.FileNames);
        }
    }

    private async Task ExportAsync()
    {
        var document = Current;
        if (document is null)
        {
            return;
        }

        var selected = document.SelectedAssets;
        if (selected.Count == 0)
        {
            ShowToast("请先勾选要导出的图片", ToastKind.Warning);
            return;
        }

        var folder = string.IsNullOrWhiteSpace(TargetFolder)
            ? ImageExporter.DefaultTargetFolder(document.Model)
            : TargetFolder;

        IsBusy = true;
        BusyText = "准备导出…";
        ProgressPercent = 0;

        var exportDate = DateTime.Today;

        try
        {
            var progress = new Progress<ImageExportProgress>(p =>
            {
                ProgressPercent = p.Total == 0 ? 0 : p.Completed * 100d / p.Total;
                BusyText = $"正在导出 {p.CurrentFile}（{p.Completed}/{p.Total}）";
            });

            var result = await Task.Run(() => ImageExporter.Export(
                document.Model,
                selected,
                folder,
                exportDate,
                progress)).ConfigureAwait(true);

            document.RefreshExportNames(exportDate);
            LastExportFolder = result.Folder;
            HasExportResult = true;
            TargetFolder = result.Folder;

            if (result.Failures.Count == 0)
            {
                ShowToast($"已导出 {result.ExportedCount} 张图片到「{Path.GetFileName(result.Folder)}」", ToastKind.Success);
            }
            else
            {
                ShowToast($"已导出 {result.ExportedCount} 张，{result.Failures.Count} 张失败", ToastKind.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"导出失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            ProgressPercent = 0;
            UpdateCommandStates();
        }
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择图片保存位置",
            Multiselect = false,
            InitialDirectory = Directory.Exists(TargetFolder) ? TargetFolder : ImageExporter.DesktopPath,
        };

        if (dialog.ShowDialog() == true)
        {
            TargetFolder = Path.Combine(dialog.FolderName, ImageExporter.DefaultFolderName(Current!.Model));
        }
    }

    private void OpenFolder()
    {
        if (!Directory.Exists(LastExportFolder))
        {
            ShowToast("文件夹不存在，可能已被移动", ToastKind.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LastExportFolder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowToast("无法打开资源管理器", ToastKind.Error);
        }
    }

    private void OnImageSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedImageCount));
        ExportCommand.RaiseCanExecuteChanged();
    }

    private void UpdateCommandStates()
    {
        ExportCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedImageCount));
        Current?.RefreshSelectionState();
    }
}