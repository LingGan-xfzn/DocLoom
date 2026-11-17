using System.Globalization;
using System.Text;

namespace DocLoom.Core.Analysis;

/// <summary>文本清洗与排版度量的公共工具。</summary>
public static class TextUtils
{
    private static readonly char[] ZeroWidth = { '\u200B', '\u200C', '\u200D', '\uFEFF', '\u00AD' };

    /// <summary>把 Word 里各种空白变体压平成普通空格，并去掉零宽字符。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            if (Array.IndexOf(ZeroWidth, ch) >= 0)
            {
                continue;
            }

            if (ch is '\u3000' or '\u00A0' or '\t' or '\u2002' or '\u2003' or '\u2009' or '\u202F')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                while (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }

                sb.Append('\n');
                lastWasSpace = false;
                continue;
            }

            if (ch == ' ' && (sb.Length == 0 || lastWasSpace))
            {
                continue;
            }

            sb.Append(ch);
            lastWasSpace = ch == ' ';
        }

        return sb.ToString().Trim();
    }

    /// <summary>只保留一行内的空白压缩，用于标题与署名。</summary>
    public static string CollapseInline(string? value)
    {
        var normalized = Normalize(value).Replace('\n', ' ');
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized.Trim();
    }

    /// <summary>估算视觉宽度：中日韩字符按 1 计。</summary>
    public static int VisualLength(string value) => value.Count(c => !char.IsWhiteSpace(c));

    public static bool HasSentenceEnding(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var tail = value.TrimEnd(' ', '"', '\u201D', ')', '\uFF09', '。', '，', '；');
        return value.EndsWith('。') || value.EndsWith('；') || value.EndsWith('！') || value.EndsWith('？')
               || value.EndsWith('”') && tail.Length > 0;
    }

    /// <summary>判断是否为「纯标题式」短句：无句号、无逗号堆叠的叙述。</summary>
    public static bool LooksLikeHeading(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > 80)
        {
            return false;
        }

        if (text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？'))
        {
            return false;
        }

        var commaCount = text.Count(c => c is '，' or ',');
        return commaCount <= 2;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d / 1024d:0.00} GB");
        }

        if (bytes >= 1024L * 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d:0.0} MB");
        }

        if (bytes >= 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0} KB");
        }

        return $"{bytes} B";
    }

    /// <summary>把可能含非法字符的名称变成安全文件名。</summary>
    public static string SanitizeFileName(string name, string fallback = "未命名")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? fallback : result;
    }
}