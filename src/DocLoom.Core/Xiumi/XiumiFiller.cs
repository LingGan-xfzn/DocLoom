using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocLoom.Core.Model;

namespace DocLoom.Core.Xiumi;

public sealed class XiumiFillResult
{
    public required JsonObject Data { get; init; }
    public required XiumiTemplateMap Map { get; init; }
    public List<string> Notes { get; } = new();

    public int ParagraphsFilled { get; set; }
    public int ImagesFilled { get; set; }
    public int TemplateImagesKept { get; set; }
    public int DocumentImagesDropped { get; set; }
    public bool SignatureFilled { get; set; }
}

/// <summary>
/// 把文档内容灌进模板槽位。
/// 只改文本与图片地址，组件样式、布局、装饰一概不动；
/// 最后剥离 _$raHTML 渲染缓存，让秀米编辑器按新内容重新渲染。
/// </summary>
public static partial class XiumiFiller
{
    [GeneratedRegex(@"<p[^>]*>(?<open>.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"^<p>\s*((?:&nbsp;|\s)+)")]
    private static partial Regex IndentRegex();

    public static XiumiFillResult Fill(
        JsonObject template,
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<string> imageUris,
        string? title,
        IReadOnlyList<AuthorEntry> authors)
    {
        var data = (JsonObject)template.DeepClone();
        var map = XiumiTemplateMap.Build(data);
        var result = new XiumiFillResult { Data = data, Map = map };

        if (map.BodyTexts.Count == 0)
        {
            result.Notes.Add("模板里没有识别到正文文本槽，已跳过文本填充。");
        }
        else
        {
            FillParagraphs(map, paragraphs, result);
        }

        FillImages(map, imageUris, result);
        FillSignature(map, authors, result);

        if (!string.IsNullOrWhiteSpace(title))
        {
            data["title"] = title.Trim();
        }

        StripRenderCache(data);
        return result;
    }

    private static void FillParagraphs(XiumiTemplateMap map, IReadOnlyList<string> paragraphs, XiumiFillResult result)
    {
        var slots = map.BodyTexts;
        var indent = DetectIndent(slots[0].Html);
        var slotCount = slots.Count;

        if (paragraphs.Count == 0)
        {
            foreach (var slot in slots)
            {
                SetText(slot, $"<p>{indent}</p>");
            }

            result.Notes.Add("文档没有正文段落。");
            return;
        }

        // 段落多于槽位：均分合并，宁可挤一点也不新增组件（新增组件要动 uuid 与约束）。
        if (paragraphs.Count > slotCount)
        {
            result.Notes.Add($"文档有 {paragraphs.Count} 段，模板只有 {slotCount} 个正文槽，已按顺序合并。");
        }
        else if (paragraphs.Count < slotCount)
        {
            result.Notes.Add($"模板有 {slotCount} 个正文槽，文档只有 {paragraphs.Count} 段，多余槽位已置空。");
        }

        var perSlot = (int)Math.Ceiling(paragraphs.Count / (double)slotCount);
        var cursor = 0;

        for (var i = 0; i < slotCount; i++)
        {
            var remainingSlots = slotCount - i;
            var remainingParagraphs = paragraphs.Count - cursor;
            var take = remainingSlots <= 1 ? remainingParagraphs : Math.Min(perSlot, Math.Max(0, remainingParagraphs - (remainingSlots - 1)));

            if (take <= 0)
            {
                SetText(slots[i], $"<p>{indent}</p>");
                continue;
            }

            var sb = new StringBuilder();
            for (var p = 0; p < take; p++)
            {
                sb.Append("<p>").Append(indent).Append(EscapeHtml(paragraphs[cursor + p].Trim())).Append("</p>");
            }

            SetText(slots[i], sb.ToString());
            result.ParagraphsFilled += take;
            cursor += take;
        }
    }

    private static void FillImages(XiumiTemplateMap map, IReadOnlyList<string> imageUris, XiumiFillResult result)
    {
        var slots = map.BodyImages;
        for (var i = 0; i < slots.Count; i++)
        {
            if (i < imageUris.Count && !string.IsNullOrWhiteSpace(imageUris[i]))
            {
                SetImage(slots[i], imageUris[i]);
                result.ImagesFilled++;
            }
            else
            {
                // 文档图不够：保留模板原图（提交前会被过户），不硬塞空图。
                result.TemplateImagesKept++;
            }
        }

        if (imageUris.Count > slots.Count)
        {
            result.DocumentImagesDropped = imageUris.Count - slots.Count;
            result.Notes.Add($"文档有 {imageUris.Count} 张图，模板只有 {slots.Count} 个正文图槽，后 {result.DocumentImagesDropped} 张未排入。");
        }
    }

    private static void FillSignature(XiumiTemplateMap map, IReadOnlyList<AuthorEntry> authors, XiumiFillResult result)
    {
        var slot = map.Signature;
        if (slot?.Node is null || authors.Count == 0)
        {
            return;
        }

        var primary = authors[0];
        var line = BuildSignatureLine(primary);

        var html = slot.Html;
        var replaced = false;
        var updated = ParagraphRegex().Replace(html, match =>
        {
            var inner = match.Groups["open"].Value;
            var plain = XiumiSlotAnalyzer.StripHtml(inner);
            if (!replaced && (plain.StartsWith("供稿", StringComparison.Ordinal) || plain.StartsWith("撰稿", StringComparison.Ordinal)))
            {
                replaced = true;
                return $"<p>{EscapeHtml(line)}</p>";
            }

            return match.Value;
        });

        if (replaced)
        {
            SetText(slot, updated);
            result.SignatureFilled = true;
        }
        else
        {
            // 模板署名块没有「供稿/撰稿」行：把文档署名追加为首行。
            SetText(slot, $"<p>{EscapeHtml(line)}</p>" + html);
            result.SignatureFilled = true;
            result.Notes.Add("模板署名块没有供稿行，已追加到最前面。");
        }
    }

    private static string BuildSignatureLine(AuthorEntry author)
    {
        var role = author.Role is "供稿人" ? "供稿" : author.Role is "撰稿人" ? "撰稿" : author.Role;
        var body = string.Join(' ', new[] { author.Department, author.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(body) ? $"{role}：{author.RawText}" : $"{role}：{body}";
    }

    private static string DetectIndent(string html)
    {
        var match = IndentRegex().Match(html);
        return match.Success ? match.Groups[1].Value : "&nbsp; &nbsp; &nbsp; ";
    }

    private static void SetText(XiumiTextSlot slot, string html)
    {
        if (slot.Node?["txt1"] is JsonObject txt)
        {
            txt["text"] = html;
        }
    }

    private static void SetImage(XiumiImageSlot slot, string uri)
    {
        if (slot.Node?["img1"] is JsonObject img)
        {
            img["src"] = uri;
        }
    }

    private static string EscapeHtml(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>递归删除预渲染缓存：编辑器会按组件树重建，留着反而显示旧内容。</summary>
    private static void StripRenderCache(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                obj.Remove("_$raHTML");
                foreach (var pair in obj.ToList())
                {
                    StripRenderCache(pair.Value);
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array)
                {
                    StripRenderCache(item);
                }

                break;
            }
        }
    }
}