using System.Windows.Media.Imaging;
using DocLoom.App.Services;
using DocLoom.Core.Model;

namespace DocLoom.App.ViewModels;

/// <summary>图片墙里的一张图：勾选状态 + 懒加载缩略图。</summary>
public sealed class ImageItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim DecodeGate = new(3, 3);

    private bool _isSelected = true;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _thumbnailFailed;
    private int _exportIndex;

    public ImageItemViewModel(ImageAsset asset, Action selectionChanged)
    {
        Asset = asset;
        _exportIndex = asset.Index;
        SelectionChanged = selectionChanged;
    }

    public ImageAsset Asset { get; }

    private Action SelectionChanged { get; }

    public int Sequence => _exportIndex;

    public string FileNamePreview => Asset.FileName;

    public string DimensionText => Asset.DimensionText;

    public string SizeText => Asset.SizeText;

    public string MetaText => $"{Asset.SizeText}  ·  {Asset.DimensionText}";

    public string? Caption => Asset.Caption;

    public bool HasCaption => !string.IsNullOrWhiteSpace(Asset.Caption);

    public string SequenceText => $"#{_exportIndex:00}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged();
            }
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool ThumbnailFailed => _thumbnailFailed;

    /// <summary>导出序号变化时刷新预览文件名（跨天导出会换日期）。</summary>
    public void SetExportSequence(int sequence, string fileName)
    {
        _exportIndex = sequence;
        Asset.FileName = fileName;
        OnPropertyChanged(nameof(Sequence));
        OnPropertyChanged(nameof(SequenceText));
        OnPropertyChanged(nameof(FileNamePreview));
    }

    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var image = await Task.Run(() => Decode(Asset)).ConfigureAwait(false);
            if (image is null)
            {
                _thumbnailFailed = true;
                OnPropertyChanged(nameof(ThumbnailFailed));
                return;
            }

            Thumbnail = image;
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static BitmapSource? Decode(ImageAsset asset)
    {
        if (asset.Extension is "emf" or "wmf")
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(asset.Data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = 520;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}