using DocLoom.App.Services;

namespace DocLoom.App.ViewModels;

/// <summary>正文中的一段，带独立复制按钮。</summary>
public sealed class ParagraphItemViewModel : ObservableObject
{
    private bool _isCopied;
    private CancellationTokenSource? _resetCts;

    public ParagraphItemViewModel(int index, string text, int imageCount, Action<string> onCopy)
    {
        Index = index;
        Text = text;
        ImageCount = imageCount;
        CopyCommand = new AsyncRelayCommand(() => CopyAsync(onCopy));
    }

    public int Index { get; }

    public string Text { get; }

    public int ImageCount { get; }

    public bool HasImages => ImageCount > 0;

    public string ImageBadge => ImageCount > 1 ? $"含 {ImageCount} 张图" : "含 1 张图";

    public int EffectiveLength => Text.Count(c => !char.IsWhiteSpace(c));

    public string IndexText => Index.ToString("00");

    public string LengthText => $"{EffectiveLength} 字";

    public AsyncRelayCommand CopyCommand { get; }

    public bool IsCopied
    {
        get => _isCopied;
        private set => SetProperty(ref _isCopied, value);
    }

    private async Task CopyAsync(Action<string> onCopy)
    {
        var ok = ClipboardService.TrySetText(Text);
        if (!ok)
        {
            onCopy("剪贴板被占用，请稍后重试");
            return;
        }

        onCopy($"已复制第 {Index} 段");
        IsCopied = true;

        _resetCts?.Cancel();
        _resetCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(1600, _resetCts.Token).ConfigureAwait(true);
            IsCopied = false;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>署名条目，同样支持一键复制。</summary>
public sealed class AuthorItemViewModel : ObservableObject
{
    private bool _isCopied;

    public AuthorItemViewModel(string role, string display, string raw, Action<string> onCopy)
    {
        Role = role;
        Display = display;
        Raw = raw;
        CopyCommand = new AsyncRelayCommand(() => CopyAsync(onCopy));
    }

    public string Role { get; }

    public string Display { get; }

    public string Raw { get; }

    public AsyncRelayCommand CopyCommand { get; }

    public bool IsCopied
    {
        get => _isCopied;
        private set => SetProperty(ref _isCopied, value);
    }

    private async Task CopyAsync(Action<string> onCopy)
    {
        var ok = ClipboardService.TrySetText(Raw);
        onCopy(ok ? $"已复制署名：{Display}" : "剪贴板被占用，请稍后重试");
        if (!ok)
        {
            return;
        }

        IsCopied = true;
        await Task.Delay(1400).ConfigureAwait(true);
        IsCopied = false;
    }
}