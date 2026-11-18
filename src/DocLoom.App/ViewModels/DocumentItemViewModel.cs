using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using DocLoom.App.Services;
using DocLoom.Core.Analysis;
using DocLoom.Core.Model;

namespace DocLoom.App.ViewModels;

/// <summary>一份已解析文档在界面上的全部呈现状态。</summary>
public sealed class DocumentItemViewModel : ObservableObject
{
    private readonly Action<string> _toast;
    private readonly Action? _selectionChanged;

    public DocumentItemViewModel(ParsedDocument document, Action<string> toast, Action? selectionChanged = null)
    {
        Model = document;
        _toast = toast;
        _selectionChanged = selectionChanged;

        foreach (var line in (document.Title ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            TitleLines.Add(line);
        }

        foreach (var author in document.Authors)
        {
            Authors.Add(new AuthorItemViewModel(author.Role, author.Display, author.RawText, toast));
        }

        foreach (var paragraph in document.Paragraphs)
        {
            Paragraphs.Add(new ParagraphItemViewModel(paragraph.Index, paragraph.Text, paragraph.ImageCount, toast));
        }

        foreach (var image in document.Images)
        {
            Images.Add(new ImageItemViewModel(image, OnImageSelectionChanged));
        }

        foreach (var caption in document.Captions)
        {
            Captions.Add(caption);
        }

        BuildBodyFlow(document);

        CopyAllCommand = new AsyncRelayCommand(CopyAllAsync);
        CopyBodyCommand = new AsyncRelayCommand(CopyBodyAsync);
        CopyTitleCommand = new AsyncRelayCommand(CopyTitleAsync);
        OpenSourceCommand = new RelayCommand(OpenSource);
        SelectAllImagesCommand = new RelayCommand(() => IsAllSelected = true);
        ClearSelectionCommand = new RelayCommand(() => IsAllSelected = false);
        InvertSelectionCommand = new RelayCommand(InvertSelection);
        RefreshSelectionState();
    }

    public ParsedDocument Model { get; }

    public ObservableCollection<string> TitleLines { get; } = new();

    public ObservableCollection<AuthorItemViewModel> Authors { get; } = new();

    public ObservableCollection<ParagraphItemViewModel> Paragraphs { get; } = new();

    public ObservableCollection<ImageItemViewModel> Images { get; } = new();

    /// <summary>正文流：段落与图片组交错，界面按它渲染并标注图片位置。</summary>
    public ObservableCollection<object> BodyFlow { get; } = new();

    public ObservableCollection<string> Captions { get; } = new();

    public AsyncRelayCommand CopyAllCommand { get; }

    public AsyncRelayCommand CopyBodyCommand { get; }

    /// <summary>只复制标题（含副标题），方便直接贴到稿件标题位。</summary>
    public AsyncRelayCommand CopyTitleCommand { get; }

    /// <summary>用系统默认程序打开原始文档。</summary>
    public RelayCommand OpenSourceCommand { get; }

    public RelayCommand SelectAllImagesCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand InvertSelectionCommand { get; }

    private void BuildBodyFlow(ParsedDocument document)
    {
        if (document.BodyFlow.Count == 0)
        {
            // 兼容：没有 flow 时退化成纯段落列表。
            foreach (var paragraph in Paragraphs)
            {
                BodyFlow.Add(paragraph);
            }

            return;
        }

        var paragraphCursor = 0;
        var imageByAsset = Images.ToDictionary(i => i.Asset, i => i);
        var totalParagraphs = Paragraphs.Count;

        foreach (var entry in document.BodyFlow)
        {
            if (entry.Kind == Core.Model.BodyFlowKind.Paragraph)
            {
                if (paragraphCursor < Paragraphs.Count)
                {
                    BodyFlow.Add(Paragraphs[paragraphCursor]);
                    paragraphCursor++;
                }

                continue;
            }

            var group = new List<ImageItemViewModel>(entry.Images.Count);
            foreach (var asset in entry.Images)
            {
                if (imageByAsset.TryGetValue(asset, out var viewModel))
                {
                    group.Add(viewModel);
                }
            }

            if (group.Count > 0)
            {
                BodyFlow.Add(new ImageGroupViewModel(group, paragraphCursor, totalParagraphs));
            }
        }
    }

    public string FileName => Model.FileName;

    public string FilePath => Model.FilePath;

    public string ExtensionLabel => Model.Extension.TrimStart('.').ToUpperInvariant();

    public string FileMetaText => $"{TextUtils.FormatSize(Model.FileSize)} · {Model.LastWriteTime:yyyy-MM-dd HH:mm}";

    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? "（未识别到标题）" : Model.Title;

    public bool HasTitle => !string.IsNullOrWhiteSpace(Model.Title);

    public string? Subtitle => Model.Subtitle;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Model.Subtitle);

    public string CharCountText => Model.TotalCharacterCount.ToString("N0");

    public string ParagraphCountText => Paragraphs.Count.ToString();

    public string ImageCountText => Images.Count.ToString();

    public string FontSizeText => $"{Model.BodyFontSizePt:0.#} pt";

    public string SelectionSummary => Images.Count == 0
        ? "暂无图片"
        : $"已选 {Images.Count(i => i.IsSelected)} / {Images.Count} 张";

    public bool HasImages => Images.Count > 0;

    public bool HasAuthors => Authors.Count > 0;

    public bool HasCaptions => Captions.Count > 0;

    public bool HasWarnings => Model.Warnings.Count > 0;

    public string WarningsText => string.Join(Environment.NewLine, Model.Warnings);

    public bool IsAllSelected
    {
        get => Images.Count > 0 && Images.All(i => i.IsSelected);
        set
        {
            foreach (var image in Images)
            {
                image.IsSelected = value;
            }

            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ImageAsset> SelectedAssets =>
        Images.Where(i => i.IsSelected).Select(i => i.Asset).ToList();

    public int SelectedCount => Images.Count(i => i.IsSelected);

    /// <summary>导出后按实际日期刷新预览文件名。</summary>
    public void RefreshExportNames(DateTime date)
    {
        var sequence = 0;
        foreach (var image in Images.Where(i => i.IsSelected))
        {
            sequence++;
            image.SetExportSequence(sequence, Core.Export.ImageExporter.BuildFileName(date, sequence, image.Asset.Extension));
        }

        RefreshSelectionState();
    }

    public async Task LoadThumbnailsAsync()
    {
        var tasks = Images.Select(i => i.EnsureThumbnailAsync());
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    public void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    private void OnImageSelectionChanged()
    {
        RefreshSelectionState();
        _selectionChanged?.Invoke();
    }

    private void InvertSelection()
    {
        foreach (var image in Images)
        {
            image.IsSelected = !image.IsSelected;
        }

        RefreshSelectionState();
    }

    private async Task CopyAllAsync()
    {
        var ok = ClipboardService.TrySetText(Model.PlainText);
        _toast(ok ? $"已复制全文（{Model.TotalCharacterCount:N0} 字）" : "剪贴板被占用，请稍后重试");
        await Task.CompletedTask;
    }

    private async Task CopyBodyAsync()
    {
        var ok = ClipboardService.TrySetText(Model.BodyText);
        _toast(ok ? "已复制正文段落" : "剪贴板被占用，请稍后重试");
        await Task.CompletedTask;
    }

    private async Task CopyTitleAsync()
    {
        var title = (Model.Title ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(Model.Subtitle))
        {
            title = title.Length == 0
                ? Model.Subtitle!.Trim()
                : title + Environment.NewLine + Model.Subtitle!.Trim();
        }

        if (title.Length == 0)
        {
            _toast("这份文档没有识别到标题");
            await Task.CompletedTask;
            return;
        }

        var ok = ClipboardService.TrySetText(title);
        _toast(ok ? "已复制标题" : "剪贴板被占用，请稍后重试");
        await Task.CompletedTask;
    }

    private void OpenSource()
    {
        if (!File.Exists(Model.FilePath))
        {
            _toast("原文档已不在该位置，可能被移动或删除");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Model.FilePath,
                UseShellExecute = true,
            });
            _toast("已用默认程序打开原文档");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _toast($"无法打开原文档：{ex.Message}");
        }
    }
}