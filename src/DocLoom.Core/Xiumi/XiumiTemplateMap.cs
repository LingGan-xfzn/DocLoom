using System.Text.Json.Nodes;

namespace DocLoom.Core.Xiumi;

/// <summary>
/// 模板的槽位地图：哪些组件是正文文本、哪些是正文配图、哪个是署名块、哪些是固定装饰。
///
/// 识别策略不依赖某一份模板的写死路径，而是：
///   1. 在所有布局列里挑「直接文本子项最多」的那一列作为主列；
///   2. 主列里按顺序把组件分成正文文本 / 署名 / 固定区（往期推荐等）；
///   3. 主列内出现在「第一个正文之后、署名之前」的图片算正文配图，其余算装饰。
/// 这样换一套同构模板仍然能用，只是主列要足够「像正文」。
/// </summary>
public sealed class XiumiTemplateMap
{
    public required JsonObject Root { get; init; }

    public List<XiumiTextSlot> BodyTexts { get; } = new();

    public List<XiumiImageSlot> BodyImages { get; } = new();

    public XiumiTextSlot? Signature { get; set; }

    public List<XiumiTextSlot> FixedTexts { get; } = new();

    public List<XiumiImageSlot> DecorImages { get; } = new();

    public string? MainColumnPath { get; private set; }

    public static XiumiTemplateMap Build(JsonObject showData)
    {
        var map = new XiumiTemplateMap { Root = showData };
        var columns = new List<(JsonArray Items, string Path)>();
        CollectColumns(showData, string.Empty, columns, 0);

        var main = columns
            .Select(c => (c.Items, c.Path, Score: c.Items.Count(HasText)))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .FirstOrDefault();

        if (main.Items is null || main.Score <= 0)
        {
            // 没有可识别的列：退化成全量槽位，交给上层提示模板不兼容。
            return map;
        }

        map.MainColumnPath = main.Path;
        var sawBody = false;
        var sawSignature = false;

        for (var i = 0; i < main.Items.Count; i++)
        {
            var item = main.Items[i] as JsonObject;
            if (item is null)
            {
                continue;
            }

            var itemPath = $"{main.Path}[{i}]";

            if (HasText(item))
            {
                var text = BuildTextSlot(item, itemPath);
                if (text is null)
                {
                    continue;
                }

                if (text.LooksLikeSignature)
                {
                    map.Signature = text;
                    sawSignature = true;
                }
                else if (XiumiSlotMap.IsBoilerplate(text))
                {
                    map.FixedTexts.Add(text);
                }
                else
                {
                    map.BodyTexts.Add(text);
                    sawBody = true;
                }

                continue;
            }

            if (HasImage(item))
            {
                var image = BuildImageSlot(item, itemPath);
                if (image is null)
                {
                    continue;
                }

                if (sawBody && !sawSignature)
                {
                    map.BodyImages.Add(image);
                }
                else
                {
                    map.DecorImages.Add(image);
                }
            }
        }

        // 主列之外的图片一律当装饰（头图 GIF、尾部二维码、往期推荐配图等）。
        var usedPaths = map.BodyImages.Select(i => i.NodePath)
            .Concat(map.DecorImages.Select(i => i.NodePath))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var slot in XiumiSlotAnalyzer.Analyze(showData).ImageSlots)
        {
            if (!usedPaths.Contains(slot.NodePath))
            {
                map.DecorImages.Add(slot);
            }
        }

        return map;
    }

    private static void CollectColumns(JsonNode? node, string path, List<(JsonArray, string)> sink, int depth)
    {
        if (depth > 12 || node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key is "col1" or "col2" or "col3" && pair.Value is JsonObject column
                    && column["items"] is JsonArray items)
                {
                    sink.Add((items, string.IsNullOrEmpty(path) ? $"{pair.Key}.items" : $"{path}.{pair.Key}.items"));
                }

                if (pair.Key is "_comp" or "style" or "_$raHTML")
                {
                    continue;
                }

                CollectColumns(pair.Value, string.IsNullOrEmpty(path) ? pair.Key : $"{path}.{pair.Key}", sink, depth + 1);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                CollectColumns(array[i], $"{path}[{i}]", sink, depth + 1);
            }
        }
    }

    private static bool HasText(JsonNode? item) =>
        item is JsonObject o && o["txt1"]?["text"]?.GetValue<string>() is { Length: > 0 };

    private static bool HasImage(JsonNode? item) =>
        item is JsonObject o && o["img1"]?["src"]?.GetValue<string>() is { Length: > 0 };

    private static XiumiTextSlot? BuildTextSlot(JsonObject item, string itemPath)
    {
        var html = item["txt1"]?["text"]?.GetValue<string>();
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var plain = XiumiSlotAnalyzer.StripHtml(html);
        return new XiumiTextSlot
        {
            NodePath = itemPath,
            TextPath = itemPath + ".txt1.text",
            TplId = item["_comp"]?["tplId"]?.GetValue<string>() ?? string.Empty,
            Html = html,
            PlainText = plain,
            Preview = plain.Length <= 48 ? plain : plain[..48] + "…",
            Node = item,
        };
    }

    private static XiumiImageSlot? BuildImageSlot(JsonObject item, string itemPath)
    {
        var src = item["img1"]?["src"]?.GetValue<string>();
        if (string.IsNullOrEmpty(src))
        {
            return null;
        }

        return new XiumiImageSlot
        {
            NodePath = itemPath,
            SourcePath = itemPath + ".img1.src",
            TplId = item["_comp"]?["tplId"]?.GetValue<string>() ?? string.Empty,
            Source = src,
            Node = item,
        };
    }
}