using System.Text;
using System.Text.Json;
using DocLoom.Core;
using DocLoom.Core.Export;
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
                default:
                    inputs.Add(args[i]);
                    break;
            }
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