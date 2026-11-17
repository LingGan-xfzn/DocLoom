using DocLoom.Core.Analysis;
using DocLoom.Core.Model;
using DocLoom.Core.Parsing;

namespace DocLoom.Core;

/// <summary>对外门面：一个文件进，一份结构化结果出。</summary>
public static class DocumentParser
{
    public static readonly string[] SupportedExtensions = { ".docx", ".docm", ".dotx", ".doc" };

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildFilterString() =>
        "Word 文档 (*.docx;*.doc;*.docm;*.dotx)|*.docx;*.doc;*.docm;*.dotx|所有文件 (*.*)|*.*";

    public static ParsedDocument Parse(string path, DateTime? exportDate = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到文件", path);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var raw = extension switch
        {
            ".docx" or ".docm" or ".dotx" => DocxReader.Read(path),
            ".doc" => DocReader.Read(path),
            _ => throw new NotSupportedException($"暂不支持 {extension} 格式，请使用 .docx 或 .doc。"),
        };

        return StructureAnalyzer.Analyze(raw, exportDate ?? DateTime.Today);
    }
}