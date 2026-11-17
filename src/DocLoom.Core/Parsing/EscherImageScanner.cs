namespace DocLoom.Core.Parsing;

/// <summary>
/// 扫描 OLE 流中的 OfficeArt（Escher）图片记录。
/// .doc 不会把图片放进媒体目录，而是内嵌在 Data/WordDocument 流的 BSE 记录里，
/// 这里按记录头定位 blip，再依靠格式自身的结构求出准确长度。
/// </summary>
internal static class EscherImageScanner
{
    private static readonly HashSet<ushort> BlipTypes = new()
    {
        0xF01A, // EMF
        0xF01B, // WMF
        0xF01C, // PICT
        0xF01D, // JPEG
        0xF01E, // PNG
        0xF01F, // DIB
        0xF029, // TIFF
        0xF02A, // JPEG (alternate)
    };

    internal readonly record struct Candidate(byte[] Data, int Offset);

    public static IEnumerable<Candidate> Scan(byte[] data)
    {
        var results = new List<Candidate>();
        var foundByRecord = false;

        for (var i = 0; i + 8 <= data.Length; i++)
        {
            var recordType = BitConverter.ToUInt16(data, i + 2);
            if (!BlipTypes.Contains(recordType))
            {
                continue;
            }

            var recordLength = BitConverter.ToUInt32(data, i + 4);
            if (recordLength < 12 || recordLength > int.MaxValue)
            {
                continue;
            }

            var payloadStart = i + 8;
            var recordEnd = (long)payloadStart + recordLength;
            if (recordEnd > data.Length)
            {
                continue;
            }

            var image = ExtractWithin(data, payloadStart, (int)recordEnd);
            if (image is not null)
            {
                results.Add(image.Value);
                foundByRecord = true;
                i = (int)recordEnd - 1;
            }
        }

        if (foundByRecord)
        {
            return results;
        }

        // 没有可识别的 BSE 记录时，退化为按格式魔数扫描。
        for (var i = 0; i + 4 < data.Length; i++)
        {
            if (!ImageProbe.IsJpeg(data.AsSpan(i)) && !ImageProbe.IsPng(data.AsSpan(i)))
            {
                continue;
            }

            // 太靠前的命中可能是流头，跳过前 16 字节的误判。
            if (i < 16)
            {
                continue;
            }

            var length = ImageProbe.MeasureImage(data, i);
            if (length <= 128 || i + length > data.Length)
            {
                continue;
            }

            results.Add(new Candidate(data.AsSpan(i, length).ToArray(), i));
            i += length - 1;
        }

        return results;
    }

    private static Candidate? ExtractWithin(byte[] data, int payloadStart, int recordEnd)
    {
        var limit = Math.Min(recordEnd, payloadStart + 64);
        for (var p = payloadStart; p + 3 < limit; p++)
        {
            var isJpeg = ImageProbe.IsJpeg(data.AsSpan(p));
            var isPng = ImageProbe.IsPng(data.AsSpan(p));
            if (!isJpeg && !isPng)
            {
                continue;
            }

            var measured = ImageProbe.MeasureImage(data, p);
            var available = recordEnd - p;
            var length = measured > 0 && measured <= available ? measured : available;
            if (length < 256)
            {
                continue;
            }

            return new Candidate(data.AsSpan(p, length).ToArray(), p);
        }

        return null;
    }
}