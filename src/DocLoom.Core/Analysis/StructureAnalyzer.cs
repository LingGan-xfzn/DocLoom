using System.Text.RegularExpressions;
using DocLoom.Core.Model;

namespace DocLoom.Core.Analysis;

/// <summary>
/// 把原始块序列切成「标题 / 副标题 / 正文段落 / 图注 / 署名 / 图片」。
/// 判定顺序刻意保持可解释：先定正文基准字号，再用排版特征识别标题，最后在尾部找署名。
/// </summary>
public static partial class StructureAnalyzer
{
    private const double DefaultBodyFontSize = 10.5;
    private const int TitleSearchWindow = 8;
    private const int TitleLooseMaxLength = 60;
    private const int TitleContinuationMaxLength = 44;
    private const int SignatureMaxLength = 44;
    private const int AuthorTailWindow = 8;
    private const int CaptionMaxLength = 28;

    /// <summary>部门名的结尾用词，用于把「马克思主义学院 钱霞」拆成部门与姓名。</summary>
    private static readonly string[] DepartmentSuffixes =
    {
        "学院", "学部", "学校", "部", "处", "办", "中心", "系", "所", "室", "科", "党委", "支部", "团委", "公司", "集团", "单位", "医院", "校区",
    };

    [GeneratedRegex(@"^(?<role>撰稿人|撰稿|供稿人|供稿|撰文|作者|文字|图文|摄影|摄像|图片|责任编辑|责编|编辑|审核|终审|复审|校对|排版|编排|来源|通讯员|记者)\s*[:：]?\s*(?<rest>.+)$")]
    private static partial Regex SignaturePattern();

    [GeneratedRegex(@"^[\(（][^\)）]{0,40}[\)）]$")]
    private static partial Regex BracketCaptionPattern();

    public static ParsedDocument Analyze(RawDocument raw, DateTime exportDate)
    {
        var blocks = raw.Blocks.Where(b => b.HasText || b.HasImage).ToList();

        var document = new ParsedDocument
        {
            FilePath = raw.FilePath,
            FileName = raw.FileName,
            Extension = raw.Extension,
            FileSize = raw.FileSize,
            LastWriteTime = raw.LastWriteTime,
            HasRichFormatting = raw.HasRichFormatting,
        };

        foreach (var warning in raw.Warnings)
        {
            document.Warnings.Add(warning);
        }

        if (blocks.Count == 0)
        {
            document.Warnings.Add("文档中没有可读内容。");
            return document;
        }

        document.BodyFontSizePt = EstimateBodyFontSize(blocks);
        var (titleIndexes, subtitleIndex) = DetectTitle(blocks, document.BodyFontSizePt);

        var titleLines = titleIndexes.Select(i => blocks[i].Text.Trim()).Where(t => t.Length > 0).ToList();
        if (titleLines.Count > 0)
        {
            document.Title = string.Join(Environment.NewLine, titleLines);
            document.TitleLines.AddRange(titleLines);
        }

        if (subtitleIndex is int subtitleBlock)
        {
            document.Subtitle = blocks[subtitleBlock].Text.Trim();
            if (document.Title is null)
            {
                document.Title = document.Subtitle;
                document.Subtitle = null;
            }
        }

        var titleSet = new HashSet<int>(titleIndexes);
        if (subtitleIndex is int s)
        {
            titleSet.Add(s);
        }

        // 署名与图注
        var authorSlots = new List<(int Index, AuthorEntry Entry)>();
        var captionSlots = new Dictionary<int, string>();
        var tailTextIndexes = Enumerable.Range(0, blocks.Count)
            .Where(i => blocks[i].HasText && !titleSet.Contains(i))
            .TakeLast(AuthorTailWindow)
            .ToHashSet();

        for (var i = 0; i < blocks.Count; i++)
        {
            if (!blocks[i].HasText || titleSet.Contains(i))
            {
                continue;
            }

            var text = blocks[i].Text;

            if (TextUtils.VisualLength(text) <= SignatureMaxLength && SignaturePattern().Match(text) is { Success: true } match)
            {
                var isTail = tailTextIndexes.Contains(i);
                var looksLikeSignature = isTail || !TextUtils.HasSentenceEnding(text);
                if (looksLikeSignature)
                {
                    var entry = BuildAuthor(match.Groups["role"].Value, match.Groups["rest"].Value, text);
                    if (entry is not null)
                    {
                        authorSlots.Add((i, entry));
                        continue;
                    }
                }
            }

            if (IsCaptionCandidate(blocks[i]))
            {
                captionSlots[i] = text.Trim();
            }
        }

        // 署名去重：同一角色且姓名一致或互相包含时，保留信息更完整的一条。
        var authorBlockIndexes = authorSlots.Select(s => s.Index).ToHashSet();
        foreach (var entry in authorSlots.Select(s => s.Entry))
        {
            var existingIndex = document.Authors.FindIndex(a => SameAuthor(a, entry));
            if (existingIndex < 0)
            {
                document.Authors.Add(entry);
            }
            else if (Score(entry) > Score(document.Authors[existingIndex]))
            {
                document.Authors[existingIndex] = entry;
            }
        }
        var captionBlockIndexes = captionSlots.Keys.ToHashSet();

        document.Captions.AddRange(captionSlots.OrderBy(kv => kv.Key).Select(kv => kv.Value));

        // 正文段落与图片
        var paragraphIndex = 0;
        var imageIndex = 0;
        var lastCaption = (string?)null;
        var pendingImages = new List<ImageAsset>();

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            if (block.HasImage)
            {
                var group = new List<ImageAsset>();
                foreach (var rawImage in block.Images)
                {
                    imageIndex++;
                    var asset = new ImageAsset
                    {
                        Index = imageIndex,
                        FileName = BuildImageName(exportDate, imageIndex, rawImage.Extension),
                        Data = rawImage.Data,
                        Extension = rawImage.Extension,
                        Width = rawImage.Width,
                        Height = rawImage.Height,
                        SourceId = rawImage.SourceId,
                        Caption = lastCaption,
                    };
                    document.Images.Add(asset);
                    pendingImages.Add(asset);
                    group.Add(asset);
                }

                if (group.Count > 0)
                {
                    document.BodyFlow.Add(new BodyFlowEntry
                    {
                        Kind = BodyFlowKind.Images,
                        Images = group,
                    });
                }

                lastCaption = null;
            }
            else if (captionSlots.TryGetValue(i, out var caption))
            {
                // 图注通常写在图片之后：回填给紧邻的图片。
                if (pendingImages.Count > 0)
                {
                    foreach (var image in pendingImages)
                    {
                        if (image.Caption is null)
                        {
                            image.Caption = caption;
                        }
                    }

                    pendingImages.Clear();
                }

                lastCaption = caption;
                continue;
            }
            else if (captionBlockIndexes.Contains(i))
            {
                continue;
            }

            if (authorBlockIndexes.Contains(i) || titleSet.Contains(i) || !block.HasText)
            {
                continue;
            }

            paragraphIndex++;
            var paragraph = new ParagraphBlock
            {
                Index = paragraphIndex,
                SourceIndex = i,
                Text = block.Text.Trim(),
                ImageCount = block.Images.Count,
                IsLead = paragraphIndex == 1 && block.Text.Length >= 40,
            };
            document.Paragraphs.Add(paragraph);
            document.BodyFlow.Add(new BodyFlowEntry
            {
                Kind = BodyFlowKind.Paragraph,
                Paragraph = paragraph,
            });
        }

        if (!raw.HasRichFormatting && document.Title is null && document.Paragraphs.Count > 0)
        {
            document.Warnings.Add("该文档缺少排版信息，未能识别标题；请手动核对首段。");
        }

        return document;
    }

    private static double EstimateBodyFontSize(List<RawBlock> blocks)
    {
        var weights = new Dictionary<double, int>();
        foreach (var block in blocks)
        {
            if (!block.HasText || block.FontSizePt is not double size)
            {
                continue;
            }

            var weight = Math.Max(1, TextUtils.VisualLength(block.Text));
            weights[size] = weights.GetValueOrDefault(size) + weight;
        }

        if (weights.Count == 0)
        {
            return DefaultBodyFontSize;
        }

        return weights.OrderByDescending(kv => kv.Value).ThenByDescending(kv => kv.Key).First().Key;
    }

    private static (List<int> TitleIndexes, int? SubtitleIndex) DetectTitle(List<RawBlock> blocks, double bodySize)
    {
        var titleIndexes = new List<int>();
        int? subtitleIndex = null;
        var inspected = 0;
        var stopped = false;

        for (var i = 0; i < blocks.Count && inspected < TitleSearchWindow && !stopped; i++)
        {
            var block = blocks[i];
            if (!block.HasText && !block.HasImage)
            {
                continue;
            }

            inspected++;

            if (block.HasImage)
            {
                break;
            }

            var text = block.Text.Trim();
            var visualLength = TextUtils.VisualLength(text);
            if (visualLength == 0)
            {
                continue;
            }

            var isHeadingStyle = LooksLikeHeadingStyle(block.StyleName) || block.OutlineLevel is <= 1;
            var isLarge = block.FontSizePt is double size && size >= bodySize + 1.5;
            var isCentered = block.Alignment == TextAlignment.Center;
            var looksHeading = TextUtils.LooksLikeHeading(text);
            var isBold = block.IsBold;

            var strongTitle = looksHeading
                              && visualLength <= TitleLooseMaxLength
                              && (isLarge || isCentered || isBold || isHeadingStyle);

            var openTitle = titleIndexes.Count == 0 && subtitleIndex is null
                            && looksHeading
                            && visualLength <= TitleLooseMaxLength
                            && !block.HasImage;

            var continuation = (titleIndexes.Count > 0 || subtitleIndex is not null)
                               && looksHeading
                               && visualLength <= TitleContinuationMaxLength
                               && (isCentered == IsCentered(blocks, titleIndexes) || isLarge)
                               && !EndsWithSentenceMark(text);

            if (!(strongTitle || openTitle || continuation))
            {
                stopped = true;
                break;
            }

            if (visualLength > 100)
            {
                stopped = true;
                break;
            }

            if (IsDashPrefixed(text) && titleIndexes.Count > 0 && subtitleIndex is null)
            {
                subtitleIndex = i;
            }
            else
            {
                titleIndexes.Add(i);
            }
        }

        return (titleIndexes, subtitleIndex);
    }

    private static bool IsCentered(List<RawBlock> blocks, List<int> titleIndexes)
    {
        var last = titleIndexes.LastOrDefault(-1);
        return last >= 0 && blocks[last].Alignment == TextAlignment.Center;
    }

    private static bool IsDashPrefixed(string text) =>
        text.StartsWith("——", StringComparison.Ordinal)
        || text.StartsWith("—", StringComparison.Ordinal)
        || text.StartsWith("–", StringComparison.Ordinal)
        || text.StartsWith("--", StringComparison.Ordinal);

    private static bool EndsWithSentenceMark(string text) =>
        text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？') || text.EndsWith('；');

    private static bool LooksLikeHeadingStyle(string? styleName)
    {
        if (string.IsNullOrWhiteSpace(styleName))
        {
            return false;
        }

        var name = styleName.Trim();
        return name.Contains("标题", StringComparison.Ordinal)
               || name.Contains("Title", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Heading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCaptionCandidate(RawBlock block)
    {
        var text = block.Text.Trim();
        var length = TextUtils.VisualLength(text);
        if (length == 0 || length > CaptionMaxLength)
        {
            return false;
        }

        if (BracketCaptionPattern().IsMatch(text))
        {
            return true;
        }

        var hasCaptionKeyword = text.Contains("照片", StringComparison.Ordinal)
                                || text.Contains("图片", StringComparison.Ordinal)
                                || text.Contains("摄影", StringComparison.Ordinal)
                                || text.Contains("摄)", StringComparison.Ordinal);

        return hasCaptionKeyword && (block.Alignment == TextAlignment.Center || block.FontSizePt < 12);
    }

    private static AuthorEntry? BuildAuthor(string role, string rest, string rawText)
    {
        var cleaned = TextUtils.CollapseInline(rest).Trim('。', '.', '；', ';', '，', ',', '、');
        if (cleaned.Length == 0)
        {
            return null;
        }

        string? department = null;
        string? name = null;

        var spaceIndex = cleaned.LastIndexOf(' ');
        if (spaceIndex > 0 && spaceIndex < cleaned.Length - 1)
        {
            var head = cleaned[..spaceIndex].Trim();
            var tail = cleaned[(spaceIndex + 1)..].Trim();
            if (tail.Length is >= 2 and <= 5 && head.Length > 0)
            {
                department = head;
                name = tail;
            }
        }

        if (name is null)
        {
            var split = SplitByDepartmentSuffix(cleaned);
            if (split is not null)
            {
                (department, name) = split.Value;
            }
        }

        if (name is null && department is null)
        {
            if (cleaned.Length <= 6)
            {
                name = cleaned;
            }
            else
            {
                department = cleaned;
            }
        }

        if (name is not null && name.Length > 8)
        {
            department ??= name;
            name = null;
        }

        return new AuthorEntry
        {
            Role = role,
            RawText = rawText.Trim(),
            Department = department,
            Name = name,
        };
    }

    private static (string Department, string? Name)? SplitByDepartmentSuffix(string text)
    {
        foreach (var suffix in DepartmentSuffixes)
        {
            var index = text.LastIndexOf(suffix, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var cut = index + suffix.Length;
            var head = text[..cut].Trim();
            var tail = text[cut..].Trim();
            if (head.Length == 0)
            {
                continue;
            }

            if (tail.Length is >= 2 and <= 4 && !tail.Any(char.IsWhiteSpace))
            {
                return (head, tail);
            }

            if (tail.Length == 0)
            {
                return (head, null);
            }
        }

        return null;
    }

    private static int Score(AuthorEntry entry) =>
        (entry.Department?.Length ?? 0) + (entry.Name?.Length ?? 0) + (entry.Department is not null ? 4 : 0);

    private static bool SameAuthor(AuthorEntry left, AuthorEntry right)
    {
        if (left.Role != right.Role)
        {
            return false;
        }

        if (string.Equals(left.Name, right.Name, StringComparison.Ordinal))
        {
            return true;
        }

        if (left.Name is null || right.Name is null)
        {
            return left.RawText.Contains(right.RawText, StringComparison.Ordinal)
                   || right.RawText.Contains(left.RawText, StringComparison.Ordinal);
        }

        return left.Name.Contains(right.Name, StringComparison.Ordinal)
               || right.Name.Contains(left.Name, StringComparison.Ordinal);
    }

    private static string BuildImageName(DateTime date, int index, string extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? "jpg" : extension.TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg")
        {
            ext = "jpg";
        }

        return $"{date:yyyyMMdd}_{index}.{ext}";
    }
}