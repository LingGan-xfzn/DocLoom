using System.Text.Json.Nodes;
using DocLoom.Core.Model;

namespace DocLoom.Core.Xiumi;

public sealed class XiumiLayoutProgress
{
    public required string Stage { get; init; }
    public int Step { get; init; }
    public int Total { get; init; }
    public string? Detail { get; init; }

    public double Percent => Total <= 0 ? 0 : Math.Min(100, Step * 100d / Total);
}

public sealed class XiumiLayoutResult
{
    public required XiumiCreateResult Created { get; init; }
    public required XiumiFillResult Fill { get; init; }
    public required RehostReport Rehost { get; init; }
    public int UploadedDocumentImages { get; init; }
    public int FailedDocumentImages { get; init; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// 一次完整排版的编排：上传文档图 → 填充模板 → 过户模板图 → 创建作品。
/// </summary>
public static class XiumiLayoutService
{
    public static async Task<XiumiLayoutResult> PublishAsync(
        XiumiClient client,
        JsonObject templateData,
        ParsedDocument document,
        string? titleOverride = null,
        IProgress<XiumiLayoutProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var title = string.IsNullOrWhiteSpace(titleOverride)
            ? (string.IsNullOrWhiteSpace(document.Title) ? document.BaseName : document.Title.Replace('\n', ' ').Trim())
            : titleOverride.Trim();

        var notes = new List<string>();

        // 1. 文档图片先上传到当前账号，拿到新地址。
        var uris = new List<string>(document.Images.Count);
        var failed = 0;

        for (var i = 0; i < document.Images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = document.Images[i];
            progress?.Report(new XiumiLayoutProgress
            {
                Stage = "上传文档图片",
                Step = i,
                Total = document.Images.Count,
                Detail = image.FileName,
            });

            try
            {
                var uri = await client.UploadImageAsync(image.Data, image.Extension, cancellationToken).ConfigureAwait(false);
                uris.Add(uri);
            }
            catch (Exception ex) when (ex is XiumiException or HttpRequestException or TaskCanceledException)
            {
                failed++;
                notes.Add($"{image.FileName} 上传失败：{ex.Message}");
            }
        }

        progress?.Report(new XiumiLayoutProgress
        {
            Stage = "填充模板",
            Step = document.Images.Count,
            Total = document.Images.Count,
        });

        // 2. 把正文、图片、署名灌进模板。
        var filled = XiumiFiller.Fill(
            templateData,
            document.Paragraphs.Select(p => p.Text).ToList(),
            uris,
            title,
            document.Authors);

        notes.AddRange(filled.Notes);

        // 3. 模板自带的账号图需要过户（跳过刚上传的与公共素材）。
        progress?.Report(new XiumiLayoutProgress { Stage = "过户模板图片", Step = 0, Total = 1 });

        var me = await client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        var rehoster = new XiumiImageRehoster(client);
        var rehostReport = await rehoster.RehostAsync(
            (JsonObject)filled.Data,
            me.UniqueUid,
            uris.ToHashSet(StringComparer.Ordinal),
            progress is null ? null : new Progress<string>(text => progress.Report(new XiumiLayoutProgress
            {
                Stage = "过户模板图片",
                Step = 0,
                Total = 1,
                Detail = text,
            })),
            cancellationToken).ConfigureAwait(false);

        notes.AddRange(rehostReport.Failed.Select(f => "模板图过户失败：" + f));

        // 4. 创建作品。
        progress?.Report(new XiumiLayoutProgress { Stage = "创建作品", Step = 0, Total = 1 });
        var created = await client.CreateShowAsync((JsonObject)filled.Data, title, "paper", cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new XiumiLayoutProgress { Stage = "完成", Step = 1, Total = 1 });

        var result = new XiumiLayoutResult
        {
            Created = created,
            Fill = filled,
            Rehost = rehostReport,
            UploadedDocumentImages = uris.Count,
            FailedDocumentImages = failed,
        };
        result.Notes.AddRange(notes);
        return result;
    }
}