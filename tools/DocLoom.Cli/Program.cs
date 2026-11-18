using System.Text;
using System.Text.Json;
using DocLoom.Core;
using DocLoom.Core.Export;
using DocLoom.Core.Xiumi;
using System.Text.Json.Nodes;
using DocLoom.Core.Model;

namespace DocLoom.Cli;

/// <summary>
/// 命令行版 DocLoom：用来批处理与验证解析结果，不依赖界面。
/// 用法：docloom &lt;文件或目录&gt; [--json] [--export] [--out 目录]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var inputs = new List<string>();
        var asJson = false;
        var export = false;
        string? outDirectory = null;
        string? xiumiKey = null;
        string? lzText = null;
        string? fillKey = null;
        string? composeKey = null;
        var useBuiltIn = false;
        string? roundTripPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    asJson = true;
                    break;
                case "--export":
                    export = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outDirectory = args[++i];
                    break;
                case "--xiumi-key" when i + 1 < args.Length:
                    xiumiKey = args[++i];
                    break;
                case "--xiumi-roundtrip" when i + 1 < args.Length:
                    roundTripPath = args[++i];
                    break;
                case "--lz" when i + 1 < args.Length:
                    lzText = args[++i];
                    break;
                case "--xiumi-fill" when i + 1 < args.Length:
                    fillKey = args[++i];
                    break;
                case "--xiumi-compose" when i + 1 < args.Length:
                    composeKey = args[++i];
                    break;
                case "--builtin-template":
                    useBuiltIn = true;
                    break;
                default:
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (composeKey is not null || useBuiltIn)
        {
            return RunXiumiCompose(composeKey, useBuiltIn, inputs.FirstOrDefault(), outDirectory);
        }

        if (fillKey is not null)
        {
            return RunXiumiFill(fillKey, inputs.FirstOrDefault(), outDirectory);
        }

        if (lzText is not null)
        {
            var packed = LzString.CompressToBase64(lzText);
            var unpacked = LzString.DecompressFromBase64(packed);
            Console.WriteLine($"in={lzText.Length} out={packed.Length} recovered={unpacked?.Length} same={unpacked == lzText}");
            Console.WriteLine(packed);
            return unpacked == lzText ? 0 : 3;
        }

        if (xiumiKey is not null)
        {
            return RunXiumiKey(xiumiKey, roundTripPath);
        }

        var files = ExpandInputs(inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("没有找到可处理的 .doc / .docx 文件。");
            return 2;
        }

        var summaries = new List<object>();
        var failures = 0;

        foreach (var file in files)
        {
            try
            {
                var document = DocumentParser.Parse(file);
                if (asJson)
                {
                    summaries.Add(ToJsonModel(document));
                    if (export)
                    {
                        var images = document.Images.ToList();
                        ImageExporter.Export(document, images, outDirectory ?? ImageExporter.DefaultTargetFolder(document), DateTime.Today);
                    }

                    continue;
                }

                PrintDocument(document);

                if (export)
                {
                    var target = outDirectory is null
                        ? ImageExporter.DefaultTargetFolder(document)
                        : Path.Combine(outDirectory, ImageExporter.DefaultFolderName(document));
                    var result = ImageExporter.Export(document, document.Images.ToList(), target, DateTime.Today);
                    Console.WriteLine($"  导出：{result.ExportedCount}/{document.Images.Count} 张 → {result.Folder}");
                    foreach (var failure in result.Failures)
                    {
                        Console.WriteLine($"  失败：{failure}");
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                if (asJson)
                {
                    summaries.Add(new { file, error = ex.Message });
                }
                else
                {
                    Console.WriteLine($"✗ {Path.GetFileName(file)} 处理失败：{ex.Message}");
                }
            }
        }

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
        }

        return failures == 0 ? 0 : 3;
    }

    private static List<string> ExpandInputs(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                    .Where(DocumentParser.IsSupported)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(input) && DocumentParser.IsSupported(input))
            {
                files.Add(input);
            }
        }

        return files;
    }

    private static void PrintDocument(ParsedDocument document)
    {
        Console.WriteLine(new string('─', 68));
        Console.WriteLine($"文件：{document.FileName}  ({FormatSize(document.FileSize)})");
        Console.WriteLine($"标题：{document.Title ?? "（未识别）"}");
        if (!string.IsNullOrWhiteSpace(document.Subtitle))
        {
            Console.WriteLine($"副题：{document.Subtitle}");
        }

        Console.WriteLine($"正文基准字号：{document.BodyFontSizePt:0.#} pt   段落：{document.Paragraphs.Count}   图片：{document.Images.Count}   字数：{document.TotalCharacterCount}");

        if (document.Authors.Count > 0)
        {
            Console.WriteLine("署名：");
            foreach (var author in document.Authors)
            {
                Console.WriteLine($"  · {author.Display}");
            }
        }
        else
        {
            Console.WriteLine("署名：（未识别）");
        }

        if (document.Captions.Count > 0)
        {
            Console.WriteLine($"图注：{string.Join(" / ", document.Captions)}");
        }

        Console.WriteLine("段落：");
        foreach (var paragraph in document.Paragraphs)
        {
            var preview = paragraph.Text.Length <= 48 ? paragraph.Text : paragraph.Text[..48] + "…";
            Console.WriteLine($"  {paragraph.Index:00}. [{paragraph.EffectiveLength,4} 字] {preview}");
        }

        if (document.Images.Count > 0)
        {
            Console.WriteLine("图片：");
            foreach (var image in document.Images)
            {
                Console.WriteLine($"  {image.FileName,-18} {image.DimensionText,-14} {image.SizeText,8}  来源 {image.SourceId}");
            }
        }

        foreach (var warning in document.Warnings)
        {
            Console.WriteLine($"注意：{warning}");
        }
    }

    private static object ToJsonModel(ParsedDocument document) => new
    {
        file = document.FilePath,
        fileName = document.FileName,
        size = document.FileSize,
        title = document.Title,
        subtitle = document.Subtitle,
        bodyFontSizePt = document.BodyFontSizePt,
        authors = document.Authors.Select(a => new { a.Role, a.Department, a.Name, a.Display }),
        captions = document.Captions,
        paragraphs = document.Paragraphs.Select(p => new { p.Index, p.Text, p.EffectiveLength, p.ImageCount }),
        images = document.Images.Select(i => new
        {
            i.Index,
            i.FileName,
            i.Extension,
            i.Width,
            i.Height,
            i.SizeBytes,
            i.SourceId,
            i.Caption,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(i.Data)),
        }),
        plainText = document.PlainText,
        warnings = document.Warnings,
    };

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, bytes / 1024d):0} KB";

    private static int RunXiumiKey(string path, string? roundTripPath)
    {
        try
        {
            var envelope = XiumiKeyCodec.Unpack(File.ReadAllText(path));
            var data = envelope.Data as JsonObject;
            var stats = data is null ? null : XiumiSlotAnalyzer.Analyze(data);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = path,
                kind = envelope.Kind,
                type = envelope.Type,
                title = envelope.Title,
                compact = envelope.Compact,
                hasData = data is not null,
                slots = stats is null ? null : new
                {
                    texts = stats.TextSlots.Count,
                    images = stats.ImageSlots.Count,
                    textPreview = stats.TextSlots.Select(t => t.Preview),
                    imageUris = stats.ImageSlots.Select(i => i.Source),
                },
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            if (roundTripPath is not null && data is not null)
            {
                var packed = XiumiKeyCodec.Pack(data, envelope.Type, envelope.Title);
                File.WriteAllText(roundTripPath, packed);
                var again = XiumiKeyCodec.Unpack(packed);
                var same = XiumiKeyCodec.ToJson(envelope.Data!) == XiumiKeyCodec.ToJson(again.Data!);
                Console.WriteLine($"roundtrip: bytes={packed.Length} identical={same}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"xiumi-key 解析失败：{ex.Message}");
            return 2;
        }
    }

    private static int RunXiumiFill(string keyPath, string? documentPath, string? outputDirectory)
    {
        try
        {
            if (documentPath is null || !File.Exists(documentPath))
            {
                Console.Error.WriteLine("需要一份 .docx 或 .doc 作为填充源");
                return 2;
            }

            var template = XiumiKeyCodec.Unpack(File.ReadAllText(keyPath));
            if (template.Data is not JsonObject templateData)
            {
                Console.Error.WriteLine("模板密钥里没有作品数据");
                return 2;
            }

            var document = DocumentParser.Parse(documentPath);
            var paragraphs = document.Paragraphs.Select(p => p.Text).ToList();
            var filled = XiumiFiller.Fill(templateData, paragraphs, Array.Empty<string>(), document.Title, document.Authors);

            var report = new
            {
                document = Path.GetFileName(documentPath),
                paragraphs = paragraphs.Count,
                images = document.Images.Count,
                map = new
                {
                    mainColumn = filled.Map.MainColumnPath,
                    bodyTextSlots = filled.Map.BodyTexts.Count,
                    bodyImageSlots = filled.Map.BodyImages.Count,
                    decorImages = filled.Map.DecorImages.Count,
                    signature = filled.Map.Signature?.Preview,
                    fixedTexts = filled.Map.FixedTexts.Select(t => t.Preview),
                },
                fill = new
                {
                    filled.ParagraphsFilled,
                    filled.ImagesFilled,
                    filled.TemplateImagesKept,
                    filled.DocumentImagesDropped,
                    filled.SignatureFilled,
                },
                bodyTextPreview = XiumiTemplateMap.Build((JsonObject)filled.Data).BodyTexts
                    .Select(t => t.Preview),
                notes = filled.Notes,
            };

            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            if (outputDirectory is not null)
            {
                var keyOut = XiumiKeyCodec.Pack((JsonObject)filled.Data, template.Type, document.Title);
                var target = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(documentPath) + ".filled.xiumi-key");
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(target, keyOut);
                Console.WriteLine($"filled key -> {target} ({keyOut.Length} chars)");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"填充失败：{ex.Message}");
            return 2;
        }
    }

    private static int RunXiumiCompose(string? keyPath, bool useBuiltIn, string? documentPath, string? outputDirectory)
    {
        try
        {
            if (documentPath is null || !File.Exists(documentPath))
            {
                Console.Error.WriteLine("需要一份 .docx 或 .doc 作为排布来源");
                return 2;
            }

            var envelope = useBuiltIn || keyPath is null
                ? BuiltInTemplate.Load()
                : XiumiKeyCodec.Unpack(File.ReadAllText(keyPath));

            if (envelope.Data is not JsonObject templateData)
            {
                Console.Error.WriteLine("模板密钥里没有作品数据");
                return 2;
            }

            var document = DocumentParser.Parse(documentPath);
            var result = XiumiComposer.Compose(templateData, document, new Dictionary<int, string>(), document.Title);

            // 文档侧序列
            var docFlow = document.BodyFlow.Select(e => e.Kind == BodyFlowKind.Paragraph
                ? $"PARA[{e.Paragraph!.Index}]"
                : $"IMG×{e.Images.Count}").ToList();

            // 合成后的组件序列
            var column = FindColumn(result.Data);
            var composed = new List<string>();
            if (column is not null)
            {
                foreach (var node in column)
                {
                    if (node?["txt1"]?["text"]?.GetValue<string>() is { Length: > 0 })
                    {
                        var plain = XiumiSlotAnalyzer.StripHtml(node["txt1"]["text"]!.GetValue<string>());
                        var tag = plain.StartsWith("供稿", StringComparison.Ordinal) ? "SIGN" : "TEXT";
                        composed.Add(tag);
                    }
                    else if (node?["img1"]?["src"]?.GetValue<string>() is { Length: > 0 })
                    {
                        composed.Add("IMG");
                    }
                    else
                    {
                        composed.Add(node?["_comp"]?["tplId"]?.GetValue<string>()?.Split('/').Last() ?? "?");
                    }
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                document = Path.GetFileName(documentPath),
                template = useBuiltIn ? "builtin" : Path.GetFileName(keyPath),
                paragraphs = document.Paragraphs.Count,
                images = document.Images.Count,
                docFlow,
                composed,
                textBlocks = result.TextBlocks,
                imageBlocks = result.ImageBlocks,
                signature = result.SignatureIncluded,
                trailing = result.TrailingImageIncluded,
                cover = result.CoverUri,
                notes = result.Notes,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                var keyOut = XiumiKeyCodec.Pack(result.Data, envelope.Type, document.Title);
                var target = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(documentPath) + ".composed.xiumi-key");
                File.WriteAllText(target, keyOut);
                Console.WriteLine($"composed key -> {target} ({keyOut.Length} chars)");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"重建失败：{ex.Message}");
            return 2;
        }
    }

    private static JsonArray? FindColumn(JsonObject data)
    {
        JsonArray? best = null;
        var bestScore = -1;

        void Walk(JsonNode? node, int depth)
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
                        var score = list.Count(i => i?["txt1"]?["text"]?.GetValue<string>() is { Length: > 0 });
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = list;
                        }
                    }

                    if (pair.Key is "_comp" or "style" or "_$raHTML")
                    {
                        continue;
                    }

                    Walk(pair.Value, depth + 1);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    Walk(item, depth + 1);
                }
            }
        }

        Walk(data, 0);
        return best;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DocLoom CLI — 文档结构化拆分");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  docloom <文件或目录> [更多文件…] [选项]");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --json        以 JSON 输出解析结果");
        Console.WriteLine("  --export      同时把图片导出到桌面（文档名文件夹）");
        Console.WriteLine("  --out <目录>  指定导出根目录");
        Console.WriteLine("  -h, --help    显示帮助");
    }
}