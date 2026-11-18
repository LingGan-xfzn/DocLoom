using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocLoom.Core.Model;

namespace DocLoom.Core.Xiumi;

public sealed class XiumiComposeResult
{
    public required JsonObject Data { get; init; }
    public List<string> Notes { get; } = new();

    public int TextBlocks { get; set; }
    public int ImageBlocks { get; set; }
    public bool SignatureIncluded { get; set; }
    public bool TrailingImageIncluded { get; set; }
    public string? CoverUri { get; set; }
}

/// <summary>
/// 按文档自身的排布重建秀米组件序列。
///
/// 模板只提供「样式原型」：正文文本组件长什么样、图片组件长什么样、署名块长什么样；
/// 具体有几个文本、几张图、谁在前谁在后，一律照文档来。
/// 因此文档是「一段文字、一张图、一段文字」时，排出来就是三段组件，而不是模板示例的六段。
/// </summary>
public static partial class XiumiComposer
{
    [GeneratedRegex(@"^<p>\s*((?:&nbsp;|\s)+)")]
    private static partial Regex IndentRegex();

    [GeneratedRegex(@"<p[^>]*>(?<inner>.*?)</p>", RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    /// <summary>模板署名块里给填写者看的括号说明，例如「（这是示例…）」。成品里不该出现。</summary>
    [GeneratedRegex(@"（[^）]*(?:示例|没有提供|默认填写|按照此|自行填写|请填写|用户自己填写|统一)[^）]*）")]
    private static partial Regex NoteRegex();

    public const string DefaultIndent = "&nbsp; &nbsp; &nbsp; ";

    public static XiumiComposeResult Compose(
        JsonObject template,
        ParsedDocument document,
        IReadOnlyDictionary<int, string> imageUriByIndex,
        string? title = null,
        bool includeTrailingImage = true)
    {
        var data = (JsonObject)template.DeepClone();
        var result = new XiumiComposeResult { Data = data };

        var column = FindMainColumn(data);
        if (column is null)
        {
            result.Notes.Add("模板里找不到可用的正文列，无法重建排版。");
            return result;
        }

        var items = column.Value.Items;
        var prototypes = CollectPrototypes(items);
        if (prototypes.Text is null)
        {
            result.Notes.Add("模板里没有正文文本组件，无法重建排版。");
            return result;
        }

        var indent = DetectIndent(prototypes.Text);

        // 先取出全部原型（都是 DeepClone 出来的独立对象），再清空目标数组，
        // 否则把已挂在临时数组上的节点搬进来会触发「节点已有父节点」。
        items.Clear();

        // 1. 照文档的正文流逐块生成组件：一段文字一个组件，一张图一个组件。
        foreach (var entry in document.BodyFlow)
        {
            switch (entry.Kind)
            {
                case BodyFlowKind.Paragraph when entry.Paragraph is not null:
                {
                    var text = entry.Paragraph.Text.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var node = CloneWithNewUuid(prototypes.Text);
                    SetText(node, $"<p>{indent}{EscapeHtml(text)}</p>");
                    items.Add(node);
                    result.TextBlocks++;
                    break;
                }

                case BodyFlowKind.Images:
                {
                    if (prototypes.Image is null)
                    {
                        result.Notes.Add("模板里没有图片组件，文档中的图片未排入。");
                        break;
                    }

                    foreach (var asset in entry.Images)
                    {
                        var node = CloneWithNewUuid(prototypes.Image);
                        if (imageUriByIndex.TryGetValue(asset.Index, out var uri) && !string.IsNullOrWhiteSpace(uri))
                        {
                            SetImage(node, uri);
                        }

                        items.Add(node);
                        result.ImageBlocks++;
                    }

                    break;
                }
            }
        }

        if (document.BodyFlow.Count == 0 && document.Paragraphs.Count > 0)
        {
            // 兜底：没有正文流时按段落顺序补上。
            foreach (var paragraph in document.Paragraphs)
            {
                var text = paragraph.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var node = CloneWithNewUuid(prototypes.Text);
                SetText(node, $"<p>{indent}{EscapeHtml(text)}</p>");
                items.Add(node);
                result.TextBlocks++;
            }
        }

        // 2. 固定区：往期推荐、分割线等排版结构原样保留，清掉给填写者看的示例文字。
        foreach (var block in prototypes.FixedBlocks)
        {
            var clone = CloneWithNewUuid(block);
            SanitizeFixedBlock(clone);
            items.Add(clone);
        }

        // 3. 署名块：沿用模板样式，只替换供稿行。
        if (prototypes.Signature is not null)
        {
            var signature = CloneWithNewUuid(prototypes.Signature);
            if (ApplySignature(signature, document.Authors))
            {
                result.SignatureIncluded = true;
            }

            items.Add(signature);
        }

        // 3. 尾部装饰图（模板末尾那张固定图，例如二维码/logo），照旧保留。
        if (includeTrailingImage && prototypes.TrailingImage is not null)
        {
            items.Add(CloneWithNewUuid(prototypes.TrailingImage));
            result.TrailingImageIncluded = true;
        }

        // 4. 封面默认用文档第一张图（秀米需要封面）。
        var firstUri = document.Images
            .Select(i => imageUriByIndex.TryGetValue(i.Index, out var u) ? u : null)
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

        if (!string.IsNullOrWhiteSpace(firstUri))
        {
            data["cover"] = firstUri;
            result.CoverUri = firstUri;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            data["title"] = title.Trim();
        }

        StripRenderCache(data);

        result.Notes.Add($"按文档排布重建：{result.TextBlocks} 段文字、{result.ImageBlocks} 张图片。");

        if (prototypes.FixedBlocks.Count > 0)
        {
            result.Notes.Add("往期推荐区块已按模板排版保留，两个条目标题是占位文字，在秀米里替换即可。");
        }
        return result;
    }

    private readonly record struct Prototypes(
        JsonObject? Text,
        JsonObject? Image,
        JsonObject? Signature,
        JsonObject? TrailingImage,
        List<JsonObject> FixedBlocks);

    private static Prototypes CollectPrototypes(JsonArray items)
    {
        JsonObject? text = null;
        JsonObject? image = null;
        JsonObject? signature = null;
        JsonObject? trailing = null;

        var lastBodyIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (HasText(items[i]) && !IsSignature(items[i]!))
            {
                lastBodyIndex = i;
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] as JsonObject;
            if (item is null)
            {
                continue;
            }

            if (HasText(item))
            {
                if (IsSignature(item))
                {
                    signature ??= item;
                }
                else
                {
                    text ??= item;
                }

                continue;
            }

            if (HasImage(item))
            {
                if (signature is not null)
                {
                    // 署名之后的图：尾部装饰。
                    trailing ??= item;
                }
                else
                {
                    image ??= item;
                }
            }
        }

        if (text is null && lastBodyIndex >= 0)
        {
            text = items[lastBodyIndex] as JsonObject;
        }

        // 固定区：最后一个正文组件之后、署名之前的整段（往期推荐、分割线、装饰带）。
        // 这些是模板的排版结构，必须原样保留，只是里面的示例文字要清掉。
        var fixedBlocks = new List<JsonObject>();
        var bodyEnd = lastBodyIndex;
        for (var i = bodyEnd + 1; i < items.Count; i++)
        {
            if (items[i] is not JsonObject node)
            {
                continue;
            }

            if (HasText(node) && IsSignature(node))
            {
                break;
            }

            if (ReferenceEquals(node, trailing) || (HasImage(node) && signature is null))
            {
                continue;
            }

            fixedBlocks.Add(node);
        }

        return new Prototypes(text, image, signature, trailing, fixedBlocks);
    }

    private static (JsonArray Items, string Path)? FindMainColumn(JsonObject data)
    {
        var best = (Items: (JsonArray?)null, Path: string.Empty, Score: -1);

        void Walk(JsonNode? node, string path, int depth)
        {
            if (depth > 12 || node is null)
            {
                return;
            }

            if (node is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    if (pair.Key is "col1" or "col2" or "col3"
                        && pair.Value is JsonObject column
                        && column["items"] is JsonArray list)
                    {
                        var score = list.Count(HasText);
                        if (score > best.Score)
                        {
                            best = (list, path.Length == 0 ? $"{pair.Key}.items" : $"{path}.{pair.Key}.items", score);
                        }
                    }

                    if (pair.Key is "_comp" or "style" or "_$raHTML")
                    {
                        continue;
                    }

                    Walk(pair.Value, path.Length == 0 ? pair.Key : $"{path}.{pair.Key}", depth + 1);
                }
            }
            else if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", depth + 1);
                }
            }
        }

        Walk(data, string.Empty, 0);
        return best.Items is null ? null : (best.Items, best.Path);
    }

    private static bool HasText(JsonNode? node) =>
        node is JsonObject o && o["txt1"]?["text"]?.GetValue<string>() is { Length: > 0 };

    private static bool HasImage(JsonNode? node) =>
        node is JsonObject o && o["img1"]?["src"]?.GetValue<string>() is { Length: > 0 };

    private static bool IsSignature(JsonNode node)
    {
        var html = node["txt1"]?["text"]?.GetValue<string>() ?? string.Empty;
        var plain = XiumiSlotAnalyzer.StripHtml(html);
        return plain.Contains("供稿", StringComparison.Ordinal)
               || plain.Contains("撰稿", StringComparison.Ordinal)
               || plain.Contains("责编", StringComparison.Ordinal)
               || plain.Contains("审核", StringComparison.Ordinal);
    }

    /// <summary>克隆原型并重生成所有组件 id：嵌套组件的 uuid 也不能重复。</summary>
    private static JsonObject CloneWithNewUuid(JsonObject prototype)
    {
        var clone = (JsonObject)prototype.DeepClone();
        ReassignUuids(clone);
        StripRenderCache(clone);
        return clone;
    }

    private static void ReassignUuids(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj["_comp"] is JsonObject comp)
                {
                    comp["_$uuid"] = NewComponentId();
                }

                foreach (var pair in obj)
                {
                    if (pair.Key is "_comp")
                    {
                        continue;
                    }

                    ReassignUuids(pair.Value);
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array)
                {
                    ReassignUuids(item);
                }

                break;
            }
        }
    }

    private static string NewComponentId()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var buffer = new char[16];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return "comp-" + new string(buffer);
    }

    private static string DetectIndent(JsonObject textPrototype)
    {
        var html = textPrototype["txt1"]?["text"]?.GetValue<string>() ?? string.Empty;
        var match = IndentRegex().Match(html);
        return match.Success ? match.Groups[1].Value : DefaultIndent;
    }

    private static bool ApplySignature(JsonObject signature, IReadOnlyList<AuthorEntry> authors)
    {
        if (authors.Count == 0 || signature["txt1"] is not JsonObject txt)
        {
            return false;
        }

        var primary = authors[0];
        var role = primary.Role is "供稿人" ? "供稿" : primary.Role is "撰稿人" ? "撰稿" : primary.Role;
        var body = string.Join(' ', new[] { primary.Department, primary.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var line = string.IsNullOrWhiteSpace(body) ? $"{role}：{primary.RawText}" : $"{role}：{body}";

        var html = txt["text"]?.GetValue<string>() ?? string.Empty;
        var replaced = false;
        var updated = ParagraphRegex().Replace(html, match =>
        {
            var plain = XiumiSlotAnalyzer.StripHtml(match.Groups["inner"].Value);
            if (!replaced && (plain.StartsWith("供稿", StringComparison.Ordinal) || plain.StartsWith("撰稿", StringComparison.Ordinal)))
            {
                replaced = true;
                return $"<p>{EscapeHtml(line)}</p>";
            }

            return match.Value;
        });

        if (!replaced)
        {
            updated = $"<p>{EscapeHtml(line)}</p>" + html;
        }

        // 去掉模板留给填写者的括号说明，并清掉因此变空的行。
        updated = ParagraphRegex().Replace(updated, match =>
        {
            var inner = match.Groups["inner"].Value;
            var withoutNote = NoteRegex().Replace(inner, string.Empty).Trim();
            if (withoutNote.Length == 0 || withoutNote == "&nbsp;")
            {
                return string.Empty;
            }

            return withoutNote == inner.Trim() ? match.Value : $"<p>{withoutNote}</p>";
        });

        txt["text"] = updated;
        return true;
    }

    /// <summary>
    /// 固定区里的说明性文字（「这是往期推荐，用户自己填写…」）不是成品内容，
    /// 换成中性占位，等用户自己在秀米里替换；其余括号说明直接删除。
    /// </summary>
    private static void SanitizeFixedBlock(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj["txt1"] is JsonObject txt && txt["text"] is JsonValue value
                    && value.TryGetValue<string>(out var html))
                {
                    var plain = XiumiSlotAnalyzer.StripHtml(html);
                    if (plain.Contains("用户自己填写", StringComparison.Ordinal)
                        || plain.Contains("这是往期推荐", StringComparison.Ordinal))
                    {
                        txt["text"] = "<p>往期推荐标题</p>";
                    }
                    else if (NoteRegex().IsMatch(html))
                    {
                        var cleaned = NoteRegex().Replace(html, string.Empty);
                        txt["text"] = ParagraphRegex().Replace(cleaned, m =>
                        {
                            var inner = m.Groups["inner"].Value.Trim();
                            return inner.Length == 0 || inner == "&nbsp;" ? string.Empty : m.Value;
                        });
                    }
                }

                foreach (var pair in obj)
                {
                    if (pair.Key is "_comp" or "txt1" or "style" or "_$raHTML")
                    {
                        continue;
                    }

                    SanitizeFixedBlock(pair.Value);
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array)
                {
                    SanitizeFixedBlock(item);
                }

                break;
            }
        }
    }

    private static void SetText(JsonObject node, string html)
    {
        if (node["txt1"] is JsonObject txt)
        {
            txt["text"] = html;
        }
    }

    private static void SetImage(JsonObject node, string uri)
    {
        if (node["img1"] is JsonObject img)
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