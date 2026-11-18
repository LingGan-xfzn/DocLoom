using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using DocLoom.App.Services;
using DocLoom.Core.Xiumi;
using Microsoft.Win32;

namespace DocLoom.App.ViewModels;

/// <summary>「公众号排版」页的状态与动作。</summary>
public sealed class LayoutViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Func<Window?> _ownerProvider;

    private string _cookieHeader = string.Empty;
    private XiumiUser? _user;
    private JsonObject? _templateData;
    private XiumiTemplateMap? _templateMap;

    private bool _isBusy;
    private string _statusText = "就绪";
    private double _progressPercent;
    private string _accountText = "未登录";
    private string _accountDetail = "登录后才能把排版保存到你的秀米账号";
    private string? _templatePath;
    private string _templateTitle = "未选择模板";
    private string _templateSummary = "支持 .xiumi-key（秀米密钥）文件";
    private bool _hasTemplate;
    private DocumentItemViewModel? _selectedDocument;
    private string? _resultUrl;
    private string _resultTitle = string.Empty;
    private bool _hasResult;
    private string _notesText = string.Empty;
    private bool _hasNotes;

    public LayoutViewModel(MainViewModel main, Func<Window?> ownerProvider)
    {
        _main = main;
        _ownerProvider = ownerProvider;

        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new RelayCommand(Logout, () => IsLoggedIn);
        PickTemplateCommand = new AsyncRelayCommand(PickTemplateAsync);
        PublishCommand = new AsyncRelayCommand(PublishAsync, CanPublish);
        OpenResultCommand = new RelayCommand(OpenResult, () => HasResult);
        CopyResultCommand = new AsyncRelayCommand(CopyResultAsync, () => HasResult);
        OpenMyDocumentsCommand = new RelayCommand(OpenMyDocuments, () => IsLoggedIn);
    }

    public ObservableCollection<DocumentItemViewModel> Documents => _main.RecentDocuments;

    public bool IsLoggedIn => _user is not null;

    public string AccountText
    {
        get => _accountText;
        private set => SetProperty(ref _accountText, value);
    }

    public string AccountDetail
    {
        get => _accountDetail;
        private set => SetProperty(ref _accountDetail, value);
    }

    public string? TemplatePath
    {
        get => _templatePath;
        private set
        {
            if (SetProperty(ref _templatePath, value))
            {
                OnPropertyChanged(nameof(TemplatePathText));
            }
        }
    }

    public string TemplatePathText => TemplatePath ?? "尚未选择文件";

    public string TemplateTitle
    {
        get => _templateTitle;
        private set => SetProperty(ref _templateTitle, value);
    }

    public string TemplateSummary
    {
        get => _templateSummary;
        private set => SetProperty(ref _templateSummary, value);
    }

    public bool HasTemplate
    {
        get => _hasTemplate;
        private set
        {
            if (SetProperty(ref _hasTemplate, value))
            {
                RefreshCommands();
            }
        }
    }

    public DocumentItemViewModel? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (SetProperty(ref _selectedDocument, value))
            {
                OnPropertyChanged(nameof(HasSelectedDocument));
                OnPropertyChanged(nameof(SelectedDocumentSummary));
                RefreshCommands();
            }
        }
    }

    public bool HasSelectedDocument => SelectedDocument is not null;

    public string SelectedDocumentSummary => SelectedDocument is null
        ? "先在「文档处理」页拖入一篇文档，再回到这里选择"
        : $"{SelectedDocument.Model.Paragraphs.Count} 段 · {SelectedDocument.Model.Images.Count} 张图 · {SelectedDocument.Model.Paragraphs.Sum(p => p.Text.Count(c => !char.IsWhiteSpace(c)))} 字";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (SetProperty(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(ResultText));
                RefreshCommands();
            }
        }
    }

    public string? ResultUrl
    {
        get => _resultUrl;
        private set
        {
            if (SetProperty(ref _resultUrl, value))
            {
                OnPropertyChanged(nameof(ResultText));
            }
        }
    }

    public string ResultText => ResultUrl ?? "—";

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public bool HasNotes
    {
        get => _hasNotes;
        private set => SetProperty(ref _hasNotes, value);
    }

    public string NotesText
    {
        get => _notesText;
        private set => SetProperty(ref _notesText, value);
    }

    public AsyncRelayCommand LoginCommand { get; }

    public RelayCommand LogoutCommand { get; }

    public AsyncRelayCommand PickTemplateCommand { get; }

    public AsyncRelayCommand PublishCommand { get; }

    public RelayCommand OpenResultCommand { get; }

    public AsyncRelayCommand CopyResultCommand { get; }

    public RelayCommand OpenMyDocumentsCommand { get; }

    public void SelectDocument(DocumentItemViewModel? document) => SelectedDocument = document;

    private bool CanPublish() => IsLoggedIn && HasTemplate && SelectedDocument is not null && !IsBusy;

    private void RefreshCommands()
    {
        PublishCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
        OpenResultCommand.RaiseCanExecuteChanged();
        CopyResultCommand.RaiseCanExecuteChanged();
        OpenMyDocumentsCommand.RaiseCanExecuteChanged();
    }

    private async Task LoginAsync()
    {
        var owner = _ownerProvider();
        var window = new LoginWindow();
        if (owner is not null)
        {
            window.Owner = owner;
        }

        var ok = window.ShowDialog() == true;
        if (!ok || window.User is null || string.IsNullOrEmpty(window.CookieHeader))
        {
            _main.ShowToast("未完成秀米登录", ToastKind.Warning);
            return;
        }

        _cookieHeader = window.CookieHeader;
        _user = window.User;
        AccountText = $"已登录 · {_user.Nickname ?? _user.UniqueUid}";
        AccountDetail = $"账号标识 {_user.UniqueUid}";
        RefreshCommands();
        _main.ShowToast("已登录秀米", ToastKind.Success);
        await Task.CompletedTask;
    }

    private void Logout()
    {
        _cookieHeader = string.Empty;
        _user = null;
        AccountText = "未登录";
        AccountDetail = "登录后才能把排版保存到你的秀米账号";
        RefreshCommands();
        _main.ShowToast("已退出秀米账号");
    }

    private async Task PickTemplateAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择秀米模板密钥",
            Filter = "秀米密钥 (*.xiumi-key)|*.xiumi-key|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(_ownerProvider()) != true)
        {
            return;
        }

        await LoadTemplateAsync(dialog.FileName);
    }

    public async Task LoadTemplateAsync(string path)
    {
        try
        {
            var key = await Task.Run(() => File.ReadAllText(path));
            var envelope = await Task.Run(() => XiumiKeyCodec.Unpack(key));
            if (envelope.Data is not JsonObject data)
            {
                _main.ShowToast("模板密钥里没有作品数据", ToastKind.Error);
                return;
            }

            var map = await Task.Run(() => XiumiTemplateMap.Build(data));
            if (map.BodyTexts.Count == 0)
            {
                _main.ShowToast("这套模板没有可识别的正文槽位", ToastKind.Warning);
            }

            _templateData = data;
            _templateMap = map;
            TemplatePath = path;
            TemplateTitle = envelope.Title ?? Path.GetFileNameWithoutExtension(path);
            TemplateSummary = $"正文槽 {map.BodyTexts.Count} · 配图槽 {map.BodyImages.Count} · 装饰图 {map.DecorImages.Count}"
                              + (map.Signature is null ? " · 未识别署名槽" : " · 已识别署名槽");
            HasTemplate = true;
            StatusText = "模板已就绪";
            _main.ShowToast($"模板已载入：{TemplateTitle}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _templateData = null;
            _templateMap = null;
            HasTemplate = false;
            TemplateTitle = "模板解析失败";
            TemplateSummary = ex.Message;
            _main.ShowToast($"模板解析失败：{ex.Message}", ToastKind.Error);
        }
    }

    private async Task PublishAsync()
    {
        if (_templateData is null || _templateMap is null || SelectedDocument is null)
        {
            return;
        }

        IsBusy = true;
        HasResult = false;
        HasNotes = false;
        ProgressPercent = 0;
        StatusText = "准备排版…";

        var progress = new Progress<XiumiLayoutProgress>(p =>
        {
            ProgressPercent = p.Percent;
            StatusText = string.IsNullOrEmpty(p.Detail) ? p.Stage : $"{p.Stage} · {p.Detail}";
        });

        try
        {
            using var client = new XiumiClient(_cookieHeader);
            var document = SelectedDocument.Model;
            var template = (JsonObject)_templateData.DeepClone();

            var result = await XiumiLayoutService.PublishAsync(
                client,
                template,
                document,
                SelectedDocument.Model.Title,
                progress).ConfigureAwait(true);

            ResultUrl = result.Created.EditUrl;
            ResultTitle = result.Created.ShowId.ToString();
            HasResult = true;
            StatusText = "排版完成，已保存到你的秀米文档";

            if (result.Notes.Count > 0)
            {
                NotesText = string.Join(Environment.NewLine, result.Notes);
                HasNotes = true;
            }

            _main.ShowToast($"排版完成：{result.Created.ShowId}", ToastKind.Success);
        }
        catch (XiumiException ex)
        {
            StatusText = $"排版失败：{ex.Message}";
            _main.ShowToast($"排版失败：{ex.Message}", ToastKind.Error);
            if (!string.IsNullOrWhiteSpace(ex.Payload))
            {
                NotesText = $"服务端返回：{ex.Payload}";
                HasNotes = true;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"排版失败：{ex.Message}";
            _main.ShowToast($"排版失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenResult()
    {
        if (!HasResult || ResultUrl is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ResultUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _main.ShowToast("无法打开浏览器", ToastKind.Error);
        }
    }

    private async Task CopyResultAsync()
    {
        if (ResultUrl is null)
        {
            await Task.CompletedTask;
            return;
        }

        var ok = ClipboardService.TrySetText(ResultUrl);
        _main.ShowToast(ok ? "链接已复制" : "剪贴板被占用", ok ? ToastKind.Success : ToastKind.Warning);
        await Task.CompletedTask;
    }

    private void OpenMyDocuments()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://xiumi.us/studio/v5#/paper",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _main.ShowToast("无法打开浏览器", ToastKind.Error);
        }
    }
}