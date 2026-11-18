using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DocLoom.Core.Xiumi;

/// <summary>
/// 写秀米接口所需的请求体：签名、压缩、以及写入前的字段清洗。
/// 算法对齐 xiumi-key/src/lib/xiumi-api.js 的 wrapShowData / sanitizeForWrite。
/// </summary>
public static class XiumiSignature
{
    public const string Format = "c444c492de13eb854a687b3361bef8c4";

    /// <summary>跨账号沿用会自证「不是你的数据」，写入前必须剥掉。</summary>
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.Ordinal)
    {
        "show_id", "id", "version_id", "history_id", "snapshot_id", "origin_show_id",
        "user_id", "team_id", "user_sid", "unique_uid", "owner", "creator",
        "created_at", "updated_at", "deleted_at", "pub_time", "published_at", "saved_at", "last_saved_at",
        "show_data_name", "show_data_url", "editing_show_data_url", "show_url", "edit_url",
        "show_type", "type_text", "status", "state",
        "_provenance", "$appendix", "provenance", "origin_user", "origin_show",
        "desc_appendix_1", "desc_appendix_2", "desc_appendix_3",
        "raw_info_only", "signature", "format", "encodedData",
        "__ob__", "_vm", "_isVue", "_self", "_uid", "_renderProxy", "dep", "__v_isRef",
    };

    /// <summary>最小集模式额外砍掉的外围字段。</summary>
    private static readonly HashSet<string> MinimalDropKeys = new(StringComparer.Ordinal)
    {
        "cover", "backgroundMusic", "popups", "wx_author", "wx_link", "$appendix", "desc",
    };

    /// <summary>生成 POST /api/shows/v5/{type} 的请求体。</summary>
    public static JsonObject WrapShowData(JsonObject show, string uid)
    {
        var n = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var r = Random.Shared.Next(0, 1_000_001).ToString();

        var i = Md5($"{r}$${uid}$${n}");
        var signature = Md5($"{n}$${uid}$${r}");

        var data = (JsonObject)show.DeepClone();
        data["desc_appendix_1"] = n[..8] + i[8..];
        data["desc_appendix_2"] = i[..8] + n[8..];
        data["desc_appendix_3"] = r;

        return new JsonObject
        {
            ["format"] = Format,
            ["encodedData"] = LzString.CompressToBase64(XiumiKeyCodec.ToJson(data)),
            ["signature"] = signature,
        };
    }

    /// <summary>黑名单清洗：删掉一切不属于「作品内容」的字段。</summary>
    public static JsonObject SanitizeForWrite(JsonObject source, bool minimal = false)
    {
        var clone = (JsonObject)source.DeepClone();
        Sanitize(clone, minimal);
        return clone;
    }

    private static void Sanitize(JsonNode? node, bool minimal)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (ForbiddenKeys.Contains(key) || (minimal && MinimalDropKeys.Contains(key)))
                    {
                        obj.Remove(key);
                        continue;
                    }

                    Sanitize(obj[key], minimal);
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var item in array)
                {
                    Sanitize(item, minimal);
                }

                break;
            }
        }
    }

    /// <summary>把溯源信息改写为当前账号（照抄插件的 authorAppendix 处理）。</summary>
    public static void RebrandAuthorAppendix(JsonObject data, string uniqueUid, string? userSid, bool keepOrigin = false)
    {
        if (data["authorAppendix"] is not JsonObject appendix)
        {
            appendix = new JsonObject();
            data["authorAppendix"] = appendix;
        }

        if (keepOrigin)
        {
            appendix["originUserUniqueUID"] = appendix["originUserUniqueUID"]?.GetValue<string>() ?? uniqueUid;
            appendix["originUserSID"] = appendix["originUserSID"]?.GetValue<string>() ?? (userSid ?? string.Empty);
        }
        else
        {
            appendix["originUserUniqueUID"] = uniqueUid;
            appendix["originUserSID"] = userSid ?? string.Empty;
        }

        appendix["sourceUserUniqueUID"] = uniqueUid;
        appendix["sourceUserSID"] = userSid ?? string.Empty;
        appendix["creationTarget"] ??= new JsonObject();
        appendix["actions"] ??= new JsonObject();
    }

    public static string Md5(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}