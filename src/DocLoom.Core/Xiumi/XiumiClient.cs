using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace DocLoom.Core.Xiumi;

public sealed class XiumiUser
{
    public required string UniqueUid { get; init; }
    public string? UserSid { get; init; }
    public string? Nickname { get; init; }
    public string? Avatar { get; init; }
}

public sealed class XiumiException : Exception
{
    public XiumiException(string message, int? code = null, string? payload = null, string? path = null)
        : base(message)
    {
        Code = code;
        Payload = payload;
        Path = path;
    }

    public int? Code { get; }
    public string? Payload { get; }
    public string? Path { get; }

    /// <summary>-10：数据被服务端拒绝，最常见于跨账号图片或残留溯源字段。</summary>
    public bool IsDataRejected => Code == -10;
}

public sealed class XiumiCreateResult
{
    public required long ShowId { get; init; }
    public required string EditUrl { get; init; }
    public required string ListUrl { get; init; }
}

/// <summary>
/// 秀米接口客户端。登录态由调用方从 WebView2 的 CookieManager 取出后传进来，
/// 这里只负责带 cookie 发请求——原生 HTTP 没有 CORS 限制，与页面内 fetch 等价。
/// </summary>
public sealed class XiumiClient : IDisposable
{
    private const string BaseUrl = "https://xiumi.us";
    private const string StudioUrl = "https://xiumi.us/studio/v5#/paper";

    private static readonly HashSet<int> OkCodes = new() { 0, 1, 2, 3 };

    private readonly HttpClient _http;
    private readonly string _cookieHeader;

    public XiumiClient(string cookieHeader)
    {
        _cookieHeader = cookieHeader ?? string.Empty;

        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://xiumi.us/studio/v5");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", BaseUrl);
    }

    public void Dispose() => _http.Dispose();

    public async Task<XiumiUser> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/auth/me", null, cancellationToken).ConfigureAwait(false);
        var data = Unwrap(json, "/auth/me") as JsonObject
                   ?? throw new XiumiException("读取账号信息失败：响应为空");

        var user = data["user"] as JsonObject ?? data;
        var uid = user["unique_uid"]?.GetValue<string>()
                  ?? throw new XiumiException("读取账号信息失败：缺少 unique_uid");

        return new XiumiUser
        {
            UniqueUid = uid,
            UserSid = user["user_sid"]?.GetValue<string>(),
            Nickname = user["nickname"]?.GetValue<string>() ?? user["nick_name"]?.GetValue<string>(),
            Avatar = user["avatar"]?.GetValue<string>(),
        };
    }

    /// <summary>上传一张图，返回可直接写进作品数据的新地址。</summary>
    public async Task<string> UploadImageAsync(byte[] data, string extension, CancellationToken cancellationToken = default)
    {
        var mime = extension.ToLowerInvariant() switch
        {
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => "image/jpeg",
        };

        var body = new JsonObject
        {
            ["base64"] = $"data:{mime};base64,{Convert.ToBase64String(data)}",
            ["filename"] = $"{Guid.NewGuid():N}.{extension.TrimStart('.')}",
        };

        var json = await SendAsync(HttpMethod.Post, "/api/assets/image/data", body, cancellationToken).ConfigureAwait(false);
        var result = Unwrap(json, "/api/assets/image/data") as JsonObject
                     ?? throw new XiumiException("图片上传失败：响应为空");

        var uri = result["target_uri"]?.GetValue<string>()
                  ?? result["uri"]?.GetValue<string>()
                  ?? result["src"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(uri)
            ? throw new XiumiException("图片上传失败：响应里没有 target_uri")
            : uri;
    }

    /// <summary>新建一份作品（POST /api/shows/v5/{type}），失败时按最小集再试一次。</summary>
    public async Task<XiumiCreateResult> CreateShowAsync(
        JsonObject showData,
        string title,
        string type = "paper",
        CancellationToken cancellationToken = default)
    {
        var user = await GetMeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await CreateCoreAsync(showData, title, type, user, minimal: false, cancellationToken).ConfigureAwait(false);
        }
        catch (XiumiException ex) when (ex.IsDataRejected)
        {
            return await CreateCoreAsync(showData, title, type, user, minimal: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<XiumiCreateResult> CreateCoreAsync(
        JsonObject showData,
        string title,
        string type,
        XiumiUser user,
        bool minimal,
        CancellationToken cancellationToken)
    {
        var data = XiumiSignature.SanitizeForWrite(showData, minimal);
        data["title"] = title;
        XiumiSignature.RebrandAuthorAppendix(data, user.UniqueUid, user.UserSid);

        var body = XiumiSignature.WrapShowData(data, user.UniqueUid);
        var path = $"/api/shows/v5/{type}";
        var json = await SendAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        var result = Unwrap(json, path) as JsonObject
                     ?? throw new XiumiException("创建作品失败：响应为空");

        var showId = result["show_id"]?.GetValue<long>() ?? 0;
        if (showId == 0 && result["id"] is JsonNode idNode)
        {
            long.TryParse(idNode.ToJsonString(), out showId);
        }

        if (showId == 0)
        {
            throw new XiumiException("创建作品失败：服务端没有返回 show_id", payload: json);
        }

        var editUrl = result["edit_url"]?.GetValue<string>()
                      ?? $"https://xiumi.us/studio/v5#/paper/for/{showId}";

        return new XiumiCreateResult
        {
            ShowId = showId,
            EditUrl = editUrl,
            ListUrl = StudioUrl,
        };
    }

    private async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(_cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode is 401 or 403)
        {
            throw new XiumiException("登录已失效，请重新登录秀米", code: (int)response.StatusCode, payload: text, path: path);
        }

        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text))
        {
            throw new XiumiException($"接口返回 HTTP {(int)response.StatusCode}", code: (int)response.StatusCode, path: path);
        }

        return text;
    }

    private static JsonNode? Unwrap(string text, string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            throw new XiumiException("接口返回的不是 JSON", payload: text, path: path);
        }

        if (node is not JsonObject obj)
        {
            return node;
        }

        if (obj["code"] is JsonNode codeNode)
        {
            var code = codeNode.GetValue<int>();
            if (!OkCodes.Contains(code))
            {
                var message = obj["message"]?.GetValue<string>() ?? $"接口错误 code={code}";
                throw new XiumiException(message, code, text, path);
            }

            return obj["data"];
        }

        return obj;
    }
}