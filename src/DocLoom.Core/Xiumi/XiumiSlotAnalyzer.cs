using System.Text;
using System.Text.Json.Nodes;

namespace DocLoom.Core.Xiumi;

/// <summary>模板里的一个文本槽（组件上的 txt1.text，值是一小段 HTML）。</summary>
public sealed class XiumiTextSlot
{
    public required string NodePath { get; init; }
    public required string TextPath { get; init; }
    public required string TplId { get; init; }
    public required string Html { get; init; }
    public required string Preview { get; init; }
    public required string PlainText { get; init; }

    /// <summary>对应的组件节点，填充时直接改这里。</summary>
    public JsonObject? Node { get; init; }

    public bool LooksLikeSignature => PlainText.Contains("供稿", StringComparison.Ordinal)
                                     || PlainText.Contains("撰稿", StringComparison.Ordinal)
                                     || PlainText.Contains("编辑", StringComparison.Ordinal)
                                     || PlainText.Contains("总审", StringComparison.Ordinal)
                                     || PlainText.Contains("摄制", StringComparison.Ordinal);
}

/// <summary>模板里的一个图片槽（组件上的 img1.src）。</summary>
public sealed class XiumiImageSlot
{
    public required string NodePath { get; init; }
    public required string SourcePath { get; init; }
    public required string TplId { get; init; }
    public required string Source { get; init; }

    /// <summary>对应的组件节点，填充时直接改这里。</summary>
    public JsonObject? Node { get; init; }

    /// <summary>公共模板素材（statics.*），不需要过户。</summary>
    public bool IsSharedAsset => Source.Contains("statics.", StringComparison.OrdinalIgnoreCase);

    /// <summary>带账号标识（/xmi/ua/），跨账号必须过户。</summary>
    public bool NeedsRehost => Source.Contains("/xmi/ua/", StringComparison.OrdinalIgnoreCase);
}

public sealed class XiumiSlotMap
{
    public required JsonObject Data { get; init; }
    public List<XiumiTextSlot> TextSlots { get; } = new();
    public List<XiumiImageSlot> ImageSlots { get; } = new();

    /// <summary>正文文本槽：排除署名块与往期推荐等固定区。</summary>
    public IReadOnlyList<XiumiTextSlot> BodyTextSlots => TextSlots.Where(s => !s.LooksLikeSignature && !IsBoilerplate(s)).ToList();

    public IReadOnlyList<XiumiTextSlot> SignatureSlots => TextSlots.Where(s => s.LooksLikeSignature).ToList();

    public static bool IsBoilerplate(XiumiTextSlot slot) =>
        slot.PlainText.Contains("往期推荐", StringComparison.Ordinal)
        || slot.PlainText.Contains("用户自己填写", StringComparison.Ordinal);
}

/// <summary>把作品数据里的组件树摊平成槽位清单，供填充与诊断使用。</summary>
public static class XiumiSlotAnalyzer
{
    public static XiumiSlotMap Analyze(JsonObject showData)
    {
        var map = new XiumiSlotMap { Data = showData };
        Walk(showData, string.Empty, map);
        return map;
    }

    private static void Walk(JsonNode? node, string path, XiumiSlotMap map)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var tplId = obj["_comp"]?["tplId"]?.GetValue<string>();
                if (tplId is not null)
                {
                    var html = obj["txt1"]?["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        var plain = StripHtml(html);
                        map.TextSlots.Add(new XiumiTextSlot
                        {
                            NodePath = path,
                            TextPath = path + ".txt1.text",
                            TplId = tplId,
                            Html = html,
                            PlainText = plain,
                            Preview = plain.Length <= 48 ? plain : plain[..48] + "…",
                        });
                    }

                    var src = obj["img1"]?["src"]?.GetValue<string>();
                    if (src is not null)
                    {
                        map.ImageSlots.Add(new XiumiImageSlot
                        {
                            NodePath = path,
                            SourcePath = path + ".img1.src",
                            TplId = tplId,
                            Source = src,
                        });
                    }
                }

                foreach (var pair in obj)
                {
                    if (pair.Key is "_comp" or "style" or "_$raHTML")
                    {
                        continue;
                    }

                    Walk(pair.Value, string.IsNullOrEmpty(path) ? pair.Key : $"{path}.{pair.Key}", map);
                }

                break;
            }

            case JsonArray array:
            {
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], $"{path}[{i}]", map);
                }

                break;
            }
        }
    }

    public static string StripHtml(string html)
    {
        var sb = new StringBuilder(html.Length);
        var insideTag = false;
        foreach (var ch in html)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
            {
                sb.Append(ch);
            }
        }

        var text = sb.ToString()
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}