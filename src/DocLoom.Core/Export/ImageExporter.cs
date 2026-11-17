using DocLoom.Core.Analysis;
using DocLoom.Core.Model;

namespace DocLoom.Core.Export;

public sealed class ImageExportResult
{
    public required string Folder { get; init; }
    public List<string> Files { get; } = new();
    public List<string> Failures { get; } = new();
    public bool Cancelled { get; set; }

    public int ExportedCount => Files.Count;
    public bool Succeeded => Failures.Count == 0 && !Cancelled;
}

public sealed class ImageExportProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public string? CurrentFile { get; init; }
}

/// <summary>
/// 图片落盘：默认丢到桌面以文档名命名的文件夹，文件名是「日期_序号.扩展名」。
/// </summary>
public static class ImageExporter
{
    public static string DesktopPath
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            {
                return desktop;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var fallback = Path.Combine(profile, "Desktop");
            return Directory.Exists(fallback) ? fallback : profile;
        }
    }

    public static string DefaultFolderName(ParsedDocument document) =>
        TextUtils.SanitizeFileName(document.BaseName, "文档图片");

    public static string DefaultTargetFolder(ParsedDocument document) =>
        Path.Combine(DesktopPath, DefaultFolderName(document));

    public static string BuildFileName(DateTime date, int sequence, string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? "jpg" : extension.TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg")
        {
            ext = "jpg";
        }

        return $"{date:yyyyMMdd}_{sequence}.{ext}";
    }

    public static ImageExportResult Export(
        ParsedDocument document,
        IReadOnlyList<ImageAsset> images,
        string targetFolder,
        DateTime date,
        IProgress<ImageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ImageExportResult { Folder = targetFolder };

        if (images.Count == 0)
        {
            return result;
        }

        Directory.CreateDirectory(targetFolder);

        var total = images.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var image = images[i];
            var fileName = BuildFileName(date, i + 1, image.Extension);
            var fullPath = Path.Combine(targetFolder, fileName);

            try
            {
                File.WriteAllBytes(fullPath, image.Data);
                result.Files.Add(fullPath);
                image.FileName = fileName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add($"{fileName}：{ex.Message}");
            }

            progress?.Report(new ImageExportProgress
            {
                Completed = i + 1,
                Total = total,
                CurrentFile = fileName,
            });
        }

        return result;
    }
}