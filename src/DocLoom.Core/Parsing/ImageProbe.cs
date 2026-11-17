namespace DocLoom.Core.Parsing;

/// <summary>从图片字节头里读出格式与像素尺寸，不依赖任何图像库。</summary>
public static class ImageProbe
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static (string Extension, int Width, int Height) Probe(byte[] data)
    {
        if (data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            var width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            var height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return ("png", width, height);
        }

        if (IsJpeg(data))
        {
            var (w, h) = ReadJpegSize(data);
            return ("jpg", w, h);
        }

        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            var w = data[6] | (data[7] << 8);
            var h = data[8] | (data[9] << 8);
            return ("gif", w, h);
        }

        if (data.Length >= 12 && data[0] == 'B' && data[1] == 'M')
        {
            var w = BitConverter.ToInt32(data, 18);
            var h = Math.Abs(BitConverter.ToInt32(data, 22));
            return ("bmp", w, h);
        }

        if (data.Length >= 26 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            var chunk = System.Text.Encoding.ASCII.GetString(data, 12, 4);
            if (chunk == "VP8X" && data.Length >= 30)
            {
                var w = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
                var h = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
                return ("webp", w, h);
            }

            return ("webp", 0, 0);
        }

        // EMF / WMF 不是位图，统一以 emf 存出，尺寸留空。
        if (data.Length >= 4 && data[0] == 0x01 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x00)
        {
            return ("emf", 0, 0);
        }

        return ("jpg", 0, 0);
    }

    public static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(PngSignature);

    private static (int Width, int Height) ReadJpegSize(byte[] data)
    {
        var index = 2;
        while (index + 9 < data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = data[index + 1];
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                index += 2;
                continue;
            }

            var length = (data[index + 2] << 8) | data[index + 3];
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                var height = (data[index + 5] << 8) | data[index + 6];
                var width = (data[index + 7] << 8) | data[index + 8];
                return (width, height);
            }

            if (length <= 0)
            {
                break;
            }

            index += 2 + length;
        }

        return (0, 0);
    }

    /// <summary>按格式在字节流中切出完整的一张图片（用于魔数兜底扫描）。</summary>
    public static int MeasureImage(byte[] data, int start)
    {
        if (start < 0 || start >= data.Length)
        {
            return 0;
        }

        if (IsPng(data.AsSpan(start)))
        {
            return MeasurePng(data, start);
        }

        if (IsJpeg(data.AsSpan(start)))
        {
            return MeasureJpeg(data, start);
        }

        if (data[start] == 'G' && start + 10 < data.Length)
        {
            var end = data.AsSpan(start).IndexOf((byte)';');
            return end > 0 ? end + 1 : 0;
        }

        return 0;
    }

    private static int MeasurePng(byte[] data, int start)
    {
        var index = start + 8;
        while (index + 12 <= data.Length)
        {
            var length = (data[index] << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3];
            var type = System.Text.Encoding.ASCII.GetString(data, index + 4, 4);
            var chunkEnd = index + 12 + length;
            if (type == "IEND")
            {
                return Math.Min(chunkEnd, data.Length) - start;
            }

            if (length < 0 || chunkEnd <= index)
            {
                return 0;
            }

            index = chunkEnd;
        }

        return 0;
    }

    private static int MeasureJpeg(byte[] data, int start)
    {
        var index = start + 2;
        while (index + 4 <= data.Length)
        {
            if (data[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = data[index + 1];
            if (marker == 0xDA)
            {
                // 进入熵编码段，找 EOI。
                var eoi = data.AsSpan(index + 2).IndexOf(new byte[] { 0xFF, 0xD9 });
                return eoi < 0 ? 0 : index + 2 + eoi + 2 - start;
            }

            if (marker is 0xD9)
            {
                return index + 2 - start;
            }

            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                index += 2;
                continue;
            }

            var length = (data[index + 2] << 8) | data[index + 3];
            if (length <= 0)
            {
                return 0;
            }

            index += 2 + length;
        }

        return 0;
    }
}