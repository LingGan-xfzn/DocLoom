using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocLoom.Core.Analysis;
using DocLoom.Core.Model;

namespace DocLoom.Core.Parsing;

/// <summary>
/// docx（Office Open XML）读取器。直接读 ZIP + XML，不依赖 OpenXml SDK，
/// 保证发布出来的单文件 exe 没有任何第三方运行时依赖。
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static RawDocument Read(string path)
    {
        var info = new FileInfo(path);
        var warnings = new List<string>();
        var blocks = new List<RawBlock>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var documentEntry = zip.GetEntry("word/document.xml")
                            ?? throw new InvalidDataException("这不是有效的 Word 文档：缺少 word/document.xml");

        var relationships = ReadRelationships(zip, "word/_rels/document.xml.rels");
        var styles = ReadStyles(zip, warnings);
        var imageCache = new Dictionary<string, RawImage>(StringComparer.OrdinalIgnoreCase);

        using var docStream = documentEntry.Open();
        var doc = XDocument.Load(docStream);
        var body = doc.Root?.Element(W + "body");
        if (body is null)
        {
            throw new InvalidDataException("这不是有效的 Word 文档：缺少文档主体");
        }

        foreach (var node in EnumerateBodyNodes(body))
        {
            var block = BuildBlock(node, relationships, styles, zip, imageCache, warnings);
            if (block is not null)
            {
                blocks.Add(block);
            }
        }

        return new RawDocument
        {
            FilePath = path,
            FileName = info.Name,
            Extension = info.Extension.ToLowerInvariant(),
            FileSize = info.Length,
            LastWriteTime = info.LastWriteTime,
            Blocks = blocks,
            HasRichFormatting = true,
            Warnings = { },
        }.WithWarnings(warnings);
    }

    /// <summary>按文档顺序展开正文：段落、表格（单元格内段落）与内容控件，避免重复取到嵌套段落。</summary>
    private static IEnumerable<XElement> EnumerateBodyNodes(XElement container)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == W + "p")
            {
                yield return child;
            }
            else if (child.Name == W + "tbl")
            {
                foreach (var paragraph in child.Descendants(W + "p"))
                {
                    yield return paragraph;
                }
            }
            else if (child.Name == W + "sdt")
            {
                var content = child.Element(W + "sdtContent");
                if (content is not null)
                {
                    foreach (var inner in EnumerateBodyNodes(content))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    private static RawBlock? BuildBlock(
        XElement paragraph,
        IReadOnlyDictionary<string, string> relationships,
        StyleSheet styles,
        ZipArchive zip,
        Dictionary<string, RawImage> imageCache,
        List<string> warnings)
    {
        var pPr = paragraph.Element(W + "pPr");
        var styleId = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
        var styleChain = styles.Resolve(styleId);

        var text = new StringBuilder();
        var images = new List<RawImage>();
        var seenImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in paragraph.Descendants())
        {
            if (IsInsideDeletedRevision(element))
            {
                continue;
            }

            if (element.Name == W + "t")
            {
                text.Append(element.Value);
                continue;
            }

            if (element.Name == W + "tab")
            {
                text.Append(' ');
                continue;
            }

            if (element.Name == W + "br" || element.Name == W + "cr")
            {
                text.Append('\n');
                continue;
            }

            if (element.Name == W + "noBreakHyphen")
            {
                text.Append('-');
                continue;
            }

            string? relationshipId = null;
            if (element.Name == A + "blip")
            {
                relationshipId = (string?)element.Attribute(R + "embed") ?? (string?)element.Attribute(R + "link");
            }
            else if (element.Name == V + "imagedata")
            {
                relationshipId = (string?)element.Attribute(R + "id");
            }

            if (string.IsNullOrEmpty(relationshipId) || !seenImageKeys.Add(relationshipId))
            {
                continue;
            }

            var image = LoadImage(relationshipId, relationships, zip, imageCache, warnings);
            if (image is not null)
            {
                images.Add(image);
            }
        }

        var alignment = ParseAlignment((string?)pPr?.Element(W + "jc")?.Attribute(W + "val"))
                        is var parsed && parsed != TextAlignment.None
            ? parsed
            : styleChain.Alignment;

        var fontSize = ResolveFontSize(paragraph, styleChain, styles);
        var isBold = ResolveBold(paragraph, styleChain);
        var isItalic = ResolveItalic(paragraph, styleChain);
        var outline = (int?)pPr?.Element(W + "outlineLvl")?.Attribute(W + "val") ?? styleChain.OutlineLevel;
        var indent = ParseIndentPoints((string?)pPr?.Element(W + "ind")?.Attribute(W + "firstLine"));

        var blockText = TextUtils.Normalize(text.ToString());
        if (blockText.Length == 0 && images.Count == 0)
        {
            return null;
        }

        return new RawBlock
        {
            Text = blockText,
            Alignment = alignment,
            FontSizePt = fontSize,
            IsBold = isBold,
            IsItalic = isItalic,
            StyleName = styleChain.Name,
            OutlineLevel = outline,
            FirstLineIndentPt = indent,
            IsFromTable = paragraph.Ancestors(W + "tbl").Any(),
        }.WithImages(images);
    }

    private static bool IsInsideDeletedRevision(XElement element)
    {
        foreach (var ancestor in element.Ancestors())
        {
            if (ancestor.Name == W + "del")
            {
                return true;
            }
        }

        return false;
    }

    private static RawImage? LoadImage(
        string relationshipId,
        IReadOnlyDictionary<string, string> relationships,
        ZipArchive zip,
        Dictionary<string, RawImage> imageCache,
        List<string> warnings)
    {
        if (!relationships.TryGetValue(relationshipId, out var target))
        {
            return null;
        }

        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith("//", StringComparison.Ordinal))
        {
            warnings.Add("文档包含外部链接图片，已跳过（未联网下载）。");
            return null;
        }

        var entryName = ResolveEntryName("word/", target);
        if (imageCache.TryGetValue(entryName, out var cached))
        {
            return cached;
        }

        var entry = zip.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.Length == 0)
        {
            return null;
        }

        var (extension, width, height) = ImageProbe.Probe(bytes);
        if (extension == "emf" || extension == "wmf")
        {
            warnings.Add("文档包含矢量图（EMF/WMF），已按原始字节导出。");
        }

        var image = new RawImage
        {
            Data = bytes,
            SourceId = entryName,
            Extension = extension,
            Width = width,
            Height = height,
        };
        imageCache[entryName] = image;
        return image;
    }

    private static string ResolveEntryName(string baseDirectory, string target)
    {
        var normalized = target.Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            return normalized.TrimStart('/');
        }

        var parts = new List<string>(baseDirectory.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip, string entryName)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = zip.GetEntry(entryName);
        if (entry is null)
        {
            return map;
        }

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var relationship in doc.Root?.Elements(Rel + "Relationship") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
            {
                map[id] = target;
            }
        }

        return map;
    }

    private static StyleSheet ReadStyles(ZipArchive zip, List<string> warnings)
    {
        var entry = zip.GetEntry("word/styles.xml");
        if (entry is null)
        {
            return StyleSheet.Empty;
        }

        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root is null)
            {
                return StyleSheet.Empty;
            }

            double? defaultSize = null;
            var defaultRpr = root.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
            if (defaultRpr is not null)
            {
                defaultSize = ParseHalfPoints((string?)defaultRpr.Element(W + "sz")?.Attribute(W + "val"));
            }

            var styles = new Dictionary<string, StyleInfo>(StringComparer.Ordinal);
            foreach (var style in root.Elements(W + "style"))
            {
                var id = (string?)style.Attribute(W + "styleId");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var pPr = style.Element(W + "pPr");
                var rPr = style.Element(W + "rPr");
                styles[id] = new StyleInfo
                {
                    Id = id,
                    Name = (string?)style.Element(W + "name")?.Attribute(W + "val") ?? id,
                    BasedOn = (string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                    FontSizePt = ParseHalfPoints((string?)rPr?.Element(W + "sz")?.Attribute(W + "val")),
                    Bold = HasOnFlag(rPr?.Element(W + "b")),
                    Italic = HasOnFlag(rPr?.Element(W + "i")),
                    Alignment = ParseAlignment((string?)pPr?.Element(W + "jc")?.Attribute(W + "val")),
                    OutlineLevel = (int?)pPr?.Element(W + "outlineLvl")?.Attribute(W + "val"),
                    IsDefault = ((string?)style.Attribute(W + "default")) == "1",
                };
            }

            return new StyleSheet(styles, defaultSize ?? 10.5);
        }
        catch (Exception ex)
        {
            warnings.Add($"样式表读取失败，已按默认字号推断：{ex.Message}");
            return StyleSheet.Empty;
        }
    }

    private static double ResolveFontSize(XElement paragraph, StyleChain chain, StyleSheet sheet)
    {
        // 段内所有可见 run 的字号做字符数加权众数；段落标记格式作为补充。
        var weights = new Dictionary<double, int>();
        foreach (var run in paragraph.Descendants(W + "r"))
        {
            if (IsInsideDeletedRevision(run))
            {
                continue;
            }

            var runText = string.Concat(run.Elements(W + "t").Select(t => t.Value));
            var weight = Math.Max(1, runText.Length);
            var size = ParseHalfPoints((string?)run.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val"));
            if (size is not null)
            {
                weights[size.Value] = weights.GetValueOrDefault(size.Value) + weight;
            }
        }

        if (weights.Count > 0)
        {
            return weights.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;
        }

        var paragraphMark = ParseHalfPoints((string?)paragraph.Element(W + "pPr")?.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val"));
        return paragraphMark ?? chain.FontSizePt ?? sheet.DefaultFontSizePt;
    }

    private static bool ResolveBold(XElement paragraph, StyleChain chain)
    {
        var mark = paragraph.Element(W + "pPr")?.Element(W + "rPr")?.Element(W + "b");
        if (mark is not null)
        {
            return HasOnFlag(mark);
        }

        foreach (var run in paragraph.Descendants(W + "r"))
        {
            var runText = string.Concat(run.Elements(W + "t").Select(t => t.Value));
            if (runText.Trim().Length == 0)
            {
                continue;
            }

            var bold = run.Element(W + "rPr")?.Element(W + "b");
            return bold is not null ? HasOnFlag(bold) : chain.Bold;
        }

        return chain.Bold;
    }

    private static bool ResolveItalic(XElement paragraph, StyleChain chain)
    {
        foreach (var run in paragraph.Descendants(W + "r"))
        {
            var italic = run.Element(W + "rPr")?.Element(W + "i");
            if (italic is not null)
            {
                return HasOnFlag(italic);
            }
        }

        return chain.Italic;
    }

    private static bool HasOnFlag(XElement? element)
    {
        if (element is null)
        {
            return false;
        }

        var value = (string?)element.Attribute(W + "val");
        return value is null || value is "1" or "true" or "on";
    }

    private static double? ParseHalfPoints(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var half)
            ? half / 2d
            : null;

    private static double? ParseIndentPoints(string? twips) =>
        double.TryParse(twips, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value / 20d
            : null;

    private static TextAlignment ParseAlignment(string? value) => value switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "both" or "justify" => TextAlignment.Justify,
        "distribute" => TextAlignment.Distribute,
        "left" or "start" => TextAlignment.Left,
        _ => TextAlignment.None,
    };

    internal sealed record StyleInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? BasedOn { get; init; }
        public double? FontSizePt { get; init; }
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public TextAlignment Alignment { get; init; }
        public int? OutlineLevel { get; init; }
        public bool IsDefault { get; init; }
    }

    internal sealed class StyleSheet
    {
        public static readonly StyleSheet Empty = new(new Dictionary<string, StyleInfo>(), 10.5);

        private readonly Dictionary<string, StyleInfo> _styles;

        public StyleSheet(Dictionary<string, StyleInfo> styles, double defaultFontSizePt)
        {
            _styles = styles;
            DefaultFontSizePt = defaultFontSizePt;
        }

        public double DefaultFontSizePt { get; }

        public StyleChain Resolve(string? styleId)
        {
            var chain = new StyleChain();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = styleId;
            while (!string.IsNullOrEmpty(current) && visited.Add(current) && _styles.TryGetValue(current, out var info))
            {
                chain.Add(info);
                current = info.BasedOn;
            }

            if (styleId is null)
            {
                var defaultStyle = _styles.Values.FirstOrDefault(s => s.IsDefault);
                if (defaultStyle is not null)
                {
                    chain.Add(defaultStyle);
                }
            }

            return chain;
        }
    }

    internal sealed class StyleChain
    {
        private readonly List<StyleInfo> _items = new();

        public string? Name => _items.FirstOrDefault()?.Name;
        public double? FontSizePt => _items.Select(i => i.FontSizePt).FirstOrDefault(v => v is not null);
        public bool Bold => _items.Select(i => (bool?)i.Bold).FirstOrDefault(v => v is not null) ?? false;
        public bool Italic => _items.Select(i => (bool?)i.Italic).FirstOrDefault(v => v is not null) ?? false;
        public TextAlignment Alignment => _items.Select(i => i.Alignment).FirstOrDefault(a => a != TextAlignment.None);
        public int? OutlineLevel => _items.Select(i => i.OutlineLevel).FirstOrDefault(v => v is not null);

        public void Add(StyleInfo info) => _items.Add(info);
    }
}

internal static class RawBlockExtensions
{
    public static RawBlock WithImages(this RawBlock block, IEnumerable<RawImage> images)
    {
        foreach (var image in images)
        {
            block.Images.Add(image);
        }

        return block;
    }

    public static RawDocument WithWarnings(this RawDocument document, IEnumerable<string> warnings)
    {
        foreach (var warning in warnings.Distinct())
        {
            if (!document.Warnings.Contains(warning))
            {
                document.Warnings.Add(warning);
            }
        }

        return document;
    }
}