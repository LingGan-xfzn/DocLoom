namespace DocLoom.Core.Xiumi;

/// <summary>
/// 内置排版模板：随程序分发的默认版式（一套「文字 + 配图 + 署名」的可重建模板）。
/// 用户仍可换成自己的 .xiumi-key，这里只是保证开箱有一条能用的路。
/// </summary>
public static class BuiltInTemplate
{
    private const string ResourceName = "DocLoom.Core.Xiumi.BuiltInTemplate.xiumi-key";

    private static string? _cached;

    public static string Title => "内置模板 · 通讯稿三件套";

    public static string KeyText
    {
        get
        {
            if (_cached is not null)
            {
                return _cached;
            }

            using var stream = typeof(BuiltInTemplate).Assembly.GetManifestResourceStream(ResourceName)
                               ?? throw new InvalidOperationException($"内置模板资源缺失：{ResourceName}");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            _cached = reader.ReadToEnd().Trim();
            return _cached;
        }
    }

    public static XiumiKeyEnvelope Load() => XiumiKeyCodec.Unpack(KeyText);

    public static string? TryGetPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "BuiltInTemplate.xiumi-key");
        return File.Exists(path) ? path : null;
    }
}
