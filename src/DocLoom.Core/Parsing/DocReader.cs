using System.Security.Cryptography;
using System.Text;
using DocLoom.Core.Analysis;
using DocLoom.Core.Model;

namespace DocLoom.Core.Parsing;

/// <summary>
/// 老式 Word 97-2003（.doc）读取器：解析 FIB、piece table 与 Escher 图片记录。
/// 纯托管实现，不依赖 Word 或任何 Office 组件。
/// </summary>
public static class DocReader
{
    private const int FibTextOffset = 0x18;
    private const int FibTextLength = 0x1C;
    private const int FibCcpText = 0x4C;
    private const int FibFcClx = 0x1A2;
    private const int FibLcbClx = 0x1A6;
    private const char ObjectReplacement = '\uFFFC';

    private static readonly Lazy<Encoding?> Gbk = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch (Exception)
        {
            return null;
        }
    });

    public static RawDocument Read(string path)
    {
        var info = new FileInfo(path);
        var warnings = new List<string>();

        using var compound = CompoundFile.Open(path);
        var wordDocument = compound.GetStream("WordDocument")
            ?? throw new InvalidDataException("这不是有效的 Word 97-2003 文档：缺少 WordDocument 流");

        if (wordDocument.Length < 0x200 || BitConverter.ToUInt16(wordDocument, 0) != 0xA5EC)
        {
            throw new InvalidDataException("Word 文档头校验失败，文件可能已损坏");
        }

        var flags = BitConverter.ToUInt16(wordDocument, 0x0A);
        var useOneTable = (flags & 0x0200) != 0;
        var isComplex = (flags & 0x0004) != 0;
        var tableName = useOneTable ? "1Table" : "0Table";
        var tableStream = compound.GetStream(tableName);

        var text = ExtractText(wordDocument, tableStream, isComplex, warnings);
        var images = ExtractImages(compound, wordDocument, warnings);

        var blocks = BuildBlocks(text, images);
        if (blocks.Count == 0)
        {
            warnings.Add("未能在该 .doc 中读到正文文本。");
        }

        return new RawDocument
        {
            FilePath = path,
            FileName = info.Name,
            Extension = ".doc",
            FileSize = info.Length,
            LastWriteTime = info.LastWriteTime,
            Blocks = blocks,
            HasRichFormatting = false,
        }.WithWarnings(warnings);
    }

    private static string ExtractText(byte[] word, byte[]? table, bool isComplex, List<string> warnings)
    {
        var ccpText = BitConverter.ToInt32(word, FibCcpText);
        if (ccpText < 0 || ccpText > 50_000_000)
        {
            ccpText = Math.Max(0, word.Length / 2);
        }

        var hasClx = false;
        var builder = new StringBuilder(Math.Max(1024, ccpText));

        if (table is not null && table.Length >= FibLcbClx + 4)
        {
            var fcClx = BitConverter.ToInt32(word, FibFcClx);
            var lcbClx = BitConverter.ToInt32(word, FibLcbClx);
            if (fcClx > 0 && lcbClx > 0 && fcClx + lcbClx <= table.Length)
            {
                hasClx = TryReadPieceTable(word, table, fcClx, lcbClx, ccpText, builder, warnings);
            }
        }

        if (!hasClx)
        {
            var fcMin = BitConverter.ToInt32(word, FibTextOffset);
            var fcMac = BitConverter.ToInt32(word, FibTextLength);
            if (fcMin > 0 && fcMac > fcMin && fcMac <= word.Length)
            {
                var span = word.AsSpan(fcMin, Math.Min(fcMac - fcMin, ccpText * 2));
                builder.Append(DecodeUnicode(span));
                warnings.Add("该 .doc 使用旧版存储格式，已按连续文本区读取。");
            }
            else if (isComplex)
            {
                warnings.Add("该 .doc 的 fragment table 无法解析，文本可能不完整。");
            }
        }

        return builder.ToString();
    }

    private static bool TryReadPieceTable(
        byte[] word,
        byte[] table,
        int fcClx,
        int lcbClx,
        int ccpText,
        StringBuilder builder,
        List<string> warnings)
    {
        var index = fcClx;
        var end = fcClx + lcbClx;
        while (index < end)
        {
            var marker = table[index];
            if (marker == 0x01)
            {
                if (index + 3 > end)
                {
                    break;
                }

                var size = BitConverter.ToUInt16(table, index + 1);
                index += 3 + size;
                continue;
            }

            if (marker != 0x02)
            {
                index++;
                continue;
            }

            if (index + 5 > end)
            {
                break;
            }

            var lcb = BitConverter.ToInt32(table, index + 1);
            var plcStart = index + 5;
            if (lcb < 4 || plcStart + lcb > table.Length)
            {
                return false;
            }

            var pieceCount = (lcb - 4) / 12;
            if (pieceCount <= 0)
            {
                return false;
            }

            var cpStart = plcStart;
            var pcdStart = plcStart + 4 * (pieceCount + 1);
            var produced = 0;

            for (var i = 0; i < pieceCount; i++)
            {
                var cpFrom = BitConverter.ToInt32(table, cpStart + i * 4);
                var cpTo = BitConverter.ToInt32(table, cpStart + (i + 1) * 4);
                var charCount = cpTo - cpFrom;
                if (charCount <= 0)
                {
                    continue;
                }

                var fcRaw = BitConverter.ToInt32(table, pcdStart + i * 8 + 2);
                var compressed = (fcRaw & 0x40000000) != 0;
                var fc = fcRaw & 0x3FFFFFFF;
                var byteOffset = compressed ? fc / 2 : fc;
                var byteLength = compressed ? charCount : charCount * 2;

                if (byteOffset < 0 || byteOffset >= word.Length)
                {
                    continue;
                }

                byteLength = Math.Min(byteLength, word.Length - byteOffset);
                var slice = word.AsSpan(byteOffset, byteLength);
                builder.Append(compressed ? DecodeAnsi(slice, warnings) : DecodeUnicode(slice));
                produced += charCount;

                if (ccpText > 0 && produced >= ccpText)
                {
                    break;
                }
            }

            return produced > 0;
        }

        return false;
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            bytes = bytes[..(bytes.Length - 1)];
        }

        return Encoding.Unicode.GetString(bytes);
    }

    private static string DecodeAnsi(ReadOnlySpan<byte> bytes, List<string> warnings)
    {
        var encoding = Gbk.Value;
        if (encoding is null)
        {
            warnings.Add("系统缺少中文代码页支持，部分压缩存储的文字可能显示异常。");
            return Encoding.Latin1.GetString(bytes);
        }

        return encoding.GetString(bytes);
    }

    private static List<RawBlock> BuildBlocks(string rawText, List<RawImage> images)
    {
        var blocks = new List<RawBlock>();
        var cleaned = CleanFields(rawText);

        var paragraphs = cleaned.Split('\r');
        var imageCursor = 0;

        foreach (var paragraph in paragraphs)
        {
            var markerCount = paragraph.Count(c => c == ObjectReplacement);
            var text = TextUtils.Normalize(paragraph.Replace(ObjectReplacement.ToString(), string.Empty));

            var blockImages = new List<RawImage>();
            for (var i = 0; i < markerCount && imageCursor < images.Count; i++, imageCursor++)
            {
                blockImages.Add(images[imageCursor]);
            }

            if (text.Length == 0 && blockImages.Count == 0)
            {
                continue;
            }

            blocks.Add(new RawBlock { Text = text }.WithImages(blockImages));
        }

        // 文本里没有图片锚点（或被裁掉）时，把剩余图片放在文末。
        while (imageCursor < images.Count)
        {
            blocks.Add(new RawBlock { Text = string.Empty }.WithImages(new[] { images[imageCursor++] }));
        }

        return blocks;
    }

    /// <summary>处理 Word 域代码：丢弃指令部分，保留结果部分；同时清理各类控制字符。</summary>
    private static string CleanFields(string text)
    {
        var output = new StringBuilder(text.Length);
        var fieldDepth = 0;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u0013':
                    fieldDepth++;
                    continue;
                case '\u0014':
                    // 指令结束，结果开始：仍处于域内，但内容要保留。
                    continue;
                case '\u0015':
                    if (fieldDepth > 0)
                    {
                        fieldDepth--;
                    }

                    continue;
                case '\u0007':
                    output.Append('\r');
                    continue;
                case '\u000B':
                case '\u000C':
                    output.Append('\r');
                    continue;
                case '\u0001':
                    output.Append(ObjectReplacement);
                    continue;
                case '\u0002':
                case '\u0005':
                case '\u0008':
                case '\u001E':
                case '\u001F':
                    continue;
                case '\u0019':
                    continue;
                default:
                    if (fieldDepth > 0 && ch is not ('\r' or '\n'))
                    {
                        // 域指令文本（如 PAGE / HYPERLINK 参数）不进入正文。
                        continue;
                    }

                    output.Append(ch);
                    continue;
            }
        }

        return output.ToString();
    }

    private static List<RawImage> ExtractImages(CompoundFile compound, byte[] wordDocument, List<string> warnings)
    {
        var results = new List<RawImage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Scan(byte[] data, string source)
        {
            foreach (var candidate in EscherImageScanner.Scan(data))
            {
                var digest = Convert.ToHexString(SHA256.HashData(candidate.Data));
                if (!seen.Add(digest))
                {
                    continue;
                }

                var (extension, width, height) = ImageProbe.Probe(candidate.Data);
                if (extension is "emf" or "wmf")
                {
                    warnings.Add("该 .doc 含有矢量图（EMF/WMF），已按原始字节导出。");
                }

                results.Add(new RawImage
                {
                    Data = candidate.Data,
                    SourceId = $"{source}@{candidate.Offset}",
                    Extension = extension,
                    Width = width,
                    Height = height,
                });
            }
        }

        var data = compound.GetStream("Data");
        if (data is { Length: > 0 })
        {
            Scan(data, "Data");
        }

        if (results.Count == 0)
        {
            Scan(wordDocument, "WordDocument");
        }

        return results;
    }
}

