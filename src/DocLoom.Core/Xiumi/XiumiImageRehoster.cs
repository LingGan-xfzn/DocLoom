using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DocLoom.Core.Parsing;

namespace DocLoom.Core.Xiumi;

/// <summary>
/// 图片过户：模板里的 `/xmi/ua/{uid}/` 图片绑在原作者账号上，直接引用会被服务端拒绝（-10）。
/// 做法是下载原图 → 上传到当前账号 → 拿到新地址 → 在整份数据里做字符串级替换
/// （含 _$raHTML 里的地址，否则保存能过但渲染还是旧图）。
/// </summary>
public sealed class XiumiImageRehoster
{
    private readonly XiumiClient _client;
    private readonly HttpClient _downloader;

    public XiumiImageRehoster(XiumiClient client)
    {
        _client = client;
        _downloader = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _downloader.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _downloader.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://xiumi.us/");
    }

    public async Task<RehostReport> RehostAsync(
        JsonObject showData,
        string currentUid,
        IReadOnlySet<string> skipUris,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new RehostReport();
        var uris = CollectImageUris(showData)
            .Where(u => !skipUris.Contains(u))
            .Where(u => NeedsRehost(u, currentUid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (uris.Count == 0)
        {
            return report;
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var uri in uris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var absolute = Normalize(uri);
            progress?.Report($"正在过户模板图片 {index}/{uris.Count}…");

            try
            {
                using var response = await _downloader.GetAsync(absolute, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    report.Failed.Add($"{Shorten(uri)}：HTTP {(int)response.StatusCode}");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    report.Failed.Add($"{Shorten(uri)}：下载到 0 字节");
                    continue;
                }

                var (extension, _, _) = ImageProbe.Probe(bytes);
                if (extension is "emf" or "wmf")
                {
                    report.Failed.Add($"{Shorten(uri)}：矢量图不支持过户");
                    continue;
                }

                var uploaded = await _client.UploadImageAsync(bytes, extension, cancellationToken).ConfigureAwait(false);
                mapping[uri] = uploaded;
                report.Moved++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or XiumiException)
            {
                report.Failed.Add($"{Shorten(uri)}：{ex.Message}");
            }
        }

        if (mapping.Count > 0)
        {
            ReplaceInStrings(showData, mapping);
            progress?.Report($"已过户 {mapping.Count} 张模板图片");
        }

        return report;
    }

    /// <summary>递归收集所有像图片地址的字符串。</summary>
    public static HashSet<string> CollectImageUris(JsonNode? node, HashSet<string>? sink = null)
    {
        sink ??= new HashSet<string>(StringComparer.Ordinal);

        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    CollectImageUris(pair.Value, sink);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    CollectImageUris(item, sink);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (var candidate in ExtractUris(text))
                {
                    sink.Add(candidate);
                }

                break;
        }

        return sink;
    }

    private static IEnumerable<string> ExtractUris(string text)
    {
        if (text.Length < 8 || text.Length > 4000)
        {
            yield break;
        }

        // 纯地址
        if (LooksLikeImage(text))
        {
            yield return text.Trim();
            yield break;
        }

        // HTML / CSS 里嵌的地址
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf("//", index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('"' or '\'' or ')' or '>'))
            {
                end++;
            }

            var candidate = text[start..end];
            if (LooksLikeImage(candidate))
            {
                yield return candidate;
            }

            index = end;
        }
    }

    private static bool LooksLikeImage(string value) =>
        (value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        && (value.Contains(".png", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".gif", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".webp", StringComparison.OrdinalIgnoreCase)
            || value.Contains("x-oss-process", StringComparison.OrdinalIgnoreCase));

    private static bool NeedsRehost(string uri, string currentUid)
    {
        if (!uri.StartsWith("//", StringComparison.Ordinal) && !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.Contains("statics.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(currentUid)
            && uri.Contains($"/xmi/ua/{currentUid}/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Contains("/xmi/ua/", StringComparison.OrdinalIgnoreCase)
               || uri.Contains("img.xiumi.us", StringComparison.OrdinalIgnoreCase)
               || uri.Contains("xiumius.cn", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string uri) =>
        uri.StartsWith("//", StringComparison.Ordinal) ? "https:" + uri : uri;

    private static void ReplaceInStrings(JsonNode? node, IReadOnlyDictionary<string, string> mapping)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    {
                        obj[key] = ReplaceAll(text, mapping);
                    }
                    else
                    {
                        ReplaceInStrings(value, mapping);
                    }
                }

                break;
            }

            case JsonArray array:
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var value = array[i];
                    if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    {
                        array[i] = ReplaceAll(text, mapping);
                    }
                    else
                    {
                        ReplaceInStrings(value, mapping);
                    }
                }

                break;
            }
        }
    }

    /// <summary>同一个地址在数据里有 `//host`、`https://host`、`http://host` 三种形态，按长度倒序替换。</summary>
    private static string ReplaceAll(string text, IReadOnlyDictionary<string, string> mapping)
    {
        var result = text;
        foreach (var pair in mapping.OrderByDescending(p => p.Key.Length))
        {
            var old = pair.Key;
            var replacement = pair.Value;

            if (old.StartsWith("//", StringComparison.Ordinal))
            {
                var hostPath = old;
                var secure = "https:" + old;
                var insecure = "http:" + old;

                result = result.Replace(secure, ToAbsoluteForm(replacement, "https:"), StringComparison.Ordinal);
                result = result.Replace(insecure, ToAbsoluteForm(replacement, "http:"), StringComparison.Ordinal);
                result = result.Replace(hostPath, replacement, StringComparison.Ordinal);
            }
            else
            {
                result = result.Replace(old, replacement, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static string ToAbsoluteForm(string replacement, string scheme) =>
        replacement.StartsWith("//", StringComparison.Ordinal) ? scheme + replacement : replacement;

    private static string Shorten(string uri) => uri.Length <= 60 ? uri : uri[..57] + "…";
}

public sealed class RehostReport
{
    public int Moved { get; set; }
    public List<string> Failed { get; } = new();
    public bool Succeeded => Failed.Count == 0;
}