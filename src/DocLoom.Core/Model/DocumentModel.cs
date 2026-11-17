namespace DocLoom.Core.Model;

/// <summary>段落在原文档中的水平对齐方式。</summary>
public enum TextAlignment
{
    None,
    Left,
    Center,
    Right,
    Justify,
    Distribute,
}

/// <summary>从文档中抽取出来的原始块（一个自然段，可能含图片）。解析器只负责忠实搬运，不做语义判断。</summary>
public sealed class RawBlock
{
    public string Text { get; init; } = string.Empty;
    public List<RawImage> Images { get; } = new();
    public TextAlignment Alignment { get; init; } = TextAlignment.None;
    public double? FontSizePt { get; init; }
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public string? StyleName { get; init; }
    public int? OutlineLevel { get; init; }
    public double? FirstLineIndentPt { get; init; }
    public bool IsFromTable { get; init; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool HasImage => Images.Count > 0;
}

/// <summary>一张从文档中抽取到的图片字节，尚未命名、尚未落盘。</summary>
public sealed class RawImage
{
    public required byte[] Data { get; init; }
    public required string SourceId { get; init; }
    public required string Extension { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>解析中层结果：文档名 + 原始块序列 + 解析过程产生的提示。</summary>
public sealed class RawDocument
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long FileSize { get; init; }
    public required DateTime LastWriteTime { get; init; }
    public required IReadOnlyList<RawBlock> Blocks { get; init; }
    public List<string> Warnings { get; } = new();
    public bool HasRichFormatting { get; init; } = true;
}

/// <summary>正文中的一段（已剥离标题、署名与图注）。</summary>
public sealed class ParagraphBlock
{
    public int Index { get; init; }
    public int SourceIndex { get; init; }
    public required string Text { get; init; }
    public int ImageCount { get; init; }
    public bool IsLead { get; init; }

    /// <summary>不含空白的字符数，用于界面显示与筛选。</summary>
    public int EffectiveLength => Text.Count(c => !char.IsWhiteSpace(c));

    public string Preview => Text.Length <= 64 ? Text : Text[..64] + "…";
}

/// <summary>文末标注解析出的写稿人/供稿单位。</summary>
public sealed class AuthorEntry
{
    public required string Role { get; init; }
    public required string RawText { get; init; }
    public string? Department { get; init; }
    public string? Name { get; init; }

    /// <summary>界面上的一行展示文本，例如「撰稿 · 马克思主义学院 钱霞」。</summary>
    public string Display
    {
        get
        {
            var body = string.Join(' ', new[] { Department, Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(body) ? $"{Role} · {RawText}" : $"{Role} · {body}";
        }
    }

    /// <summary>用于去重的身份键（角色 + 姓名 + 部门）。</summary>
    public string IdentityKey => $"{Role}|{Department}|{Name}".Replace(" ", string.Empty);
}

/// <summary>可直接导出的一张图片。</summary>
public sealed class ImageAsset
{
    public int Index { get; init; }
    public required string FileName { get; set; }
    public required byte[] Data { get; init; }
    public required string Extension { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required string SourceId { get; init; }

    /// <summary>相邻的图注提示（如「（座谈会全景）」），可能为空。图注在分析末尾回填，故可写。</summary>
    public string? Caption { get; set; }

    public long SizeBytes => Data.LongLength;

    public string DimensionText => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "未知尺寸";

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, SizeBytes / 1024d):0} KB";
}

/// <summary>结构分析后的成品：标题、正文段落、署名、图片、图注、告警。</summary>
public sealed class ParsedDocument
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long FileSize { get; init; }
    public required DateTime LastWriteTime { get; init; }

    /// <summary>标题（多行标题以换行连接）。</summary>
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string> TitleLines { get; } = new();

    public List<ParagraphBlock> Paragraphs { get; } = new();
    public List<AuthorEntry> Authors { get; } = new();
    public List<ImageAsset> Images { get; } = new();

    /// <summary>图片位置提示文本，如「（参观照片两张）」。</summary>
    public List<string> Captions { get; } = new();

    public List<string> Warnings { get; } = new();

    public double BodyFontSizePt { get; set; } = 10.5;
    public bool HasRichFormatting { get; set; } = true;

    /// <summary>文件主名，用于图片目录命名。</summary>
    public string BaseName
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(FileName);
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "文档" : name.Trim();
        }
    }

    public int TotalCharacterCount => Paragraphs.Sum(p => p.EffectiveLength) + (Title?.Length ?? 0);

    /// <summary>排版顺序的全文：标题 → 正文段落 → 署名。</summary>
    public string PlainText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title))
            {
                parts.Add(Title.Trim());
            }

            if (!string.IsNullOrWhiteSpace(Subtitle))
            {
                parts.Add(Subtitle.Trim());
            }

            foreach (var paragraph in Paragraphs)
            {
                parts.Add(paragraph.Text.Trim());
            }

            foreach (var author in Authors)
            {
                parts.Add(author.RawText.Trim());
            }

            return string.Join(Environment.NewLine + Environment.NewLine, parts.Where(p => p.Length > 0));
        }
    }

    /// <summary>仅正文段落。</summary>
    public string BodyText => string.Join(
        Environment.NewLine + Environment.NewLine,
        Paragraphs.Select(p => p.Text.Trim()).Where(p => p.Length > 0));
}