using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocLoom.Core.Xiumi;

/// <summary>一枚 xiumi-key 解出来的信封：作品数据 + 元信息。</summary>
public sealed class XiumiKeyEnvelope
{
    public string Kind { get; init; } = "show";
    public string Type { get; init; } = "paper";
    public string? Title { get; init; }
    public string? Html { get; init; }
    public bool Compact { get; init; }
    public JsonNode? Data { get; init; }
    public JsonObject? Raw { get; init; }
}

/// <summary>秀米密钥的打包 / 解包（格式见 xiumi-key/ARCHITECTURE.md §4）。</summary>
public static class XiumiKeyCodec
{
    public const string Prefix = "XMK1.";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>用户粘贴时常带上换行、空格、全角符号，这里统一清洗。</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '\r' or '\n' or '\t' or '\u3000' or '\u200B' or '\uFEFF')
            {
                continue;
            }

            sb.Append(ch switch
            {
                '\uFF0E' => '.',
                '\uFF0B' => '+',
                '\uFF0F' => '/',
                '\uFF1D' => '=',
                '\uFF0D' => '-',
                '\uFF3F' => '_',
                _ => ch,
            });
        }

        return sb.ToString().Trim('"', '\'', '`', '\u201C', '\u201D');
    }

    public static XiumiKeyEnvelope Unpack(string raw)
    {
        var key = Normalize(raw);
        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("这不是秀米密钥：缺少 XMK1. 前缀");
        }

        var body = key[Prefix.Length..];
        var standard = FromBase64Url(body);
        var json = LzString.DecompressFromBase64(standard);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("密钥解压失败：内容为空或已损坏");
        }

        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidDataException("密钥内容不是 JSON 对象");

        var kind = root["kind"]?.GetValue<string>() ?? (root["k"]?.GetValue<string>() ?? "show");
        var type = root["type"]?.GetValue<string>() ?? (root["t"]?.GetValue<string>() ?? "paper");
        var title = root["title"]?.GetValue<string>() ?? root["n"]?.GetValue<string>();
        var html = root["html"]?.GetValue<string>();
        var compact = root["compact"]?.GetValue<bool>() ?? false;
        var data = root["data"] ?? root["d"];

        return new XiumiKeyEnvelope
        {
            Kind = kind,
            Type = type,
            Title = title,
            Html = html,
            Compact = compact,
            Data = data,
            Raw = root,
        };
    }

    /// <summary>把修改后的作品数据重新打包成密钥（用于离线校验与本地备份）。</summary>
    public static string Pack(JsonObject showData, string type = "paper", string? title = null)
    {
        var resolvedTitle = title ?? showData["title"]?.GetValue<string>() ?? "DocLoom 排版";
        var (pages, comps) = Summarize(showData);

        // 字段名必须与 xiumi-key 的 codec 一致：v/k/t/n/a/c/h/s/d。
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["k"] = "xiumi-show",
            ["t"] = type,
            ["n"] = resolvedTitle,
            ["a"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["c"] = 0,
            ["h"] = ShortHash($"{resolvedTitle}|{pages}|{comps}"),
            ["s"] = "docloom",
            ["d"] = showData.DeepClone(),
        };

        var json = envelope.ToJsonString(WriteOptions);
        var compressed = LzString.CompressToBase64(json);
        return Prefix + ToBase64Url(compressed);
    }

    private static (int Pages, int Comps) Summarize(JsonObject data)
    {
        var pages = 0;
        var comps = 0;

        void CountItems(JsonArray? items)
        {
            if (items is null)
            {
                return;
            }

            foreach (var node in items)
            {
                comps++;
                if (node?["comps"]?["items"] is JsonArray nested)
                {
                    CountItems(nested);
                }
            }
        }

        if (data["cubes"] is JsonArray cubes)
        {
            foreach (var cube in cubes)
            {
                if (cube?["pages"] is not JsonArray pageList)
                {
                    continue;
                }

                pages += pageList.Count;
                foreach (var page in pageList)
                {
                    if (page?["layers"] is not JsonArray layers)
                    {
                        continue;
                    }

                    foreach (var layer in layers)
                    {
                        CountItems(layer?["comps"]?["items"] as JsonArray);
                    }
                }
            }
        }

        return (pages, comps);
    }

    /// <summary>与 codec.js 的 shortHash 一致：djb2 取 32 位、再转 base36。</summary>
    private static string ShortHash(string value)
    {
        var hash = 5381L;
        foreach (var ch in value)
        {
            hash = ((hash << 5) + hash + ch) & 0xFFFFFFFFL;
        }

        if (hash == 0)
        {
            return "0";
        }

        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        var current = hash;
        while (current > 0)
        {
            sb.Insert(0, alphabet[(int)(current % 36)]);
            current /= 36;
        }

        return sb.ToString();
    }

    public static string ToJson(JsonNode node) => node.ToJsonString(WriteOptions);

    private static string ToBase64Url(string base64) =>
        base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string FromBase64Url(string value)
    {
        var t = value.Replace('-', '+').Replace('_', '/');
        while (t.Length % 4 != 0)
        {
            t += '=';
        }

        return t;
    }
}