using System.Text;

namespace DocLoom.Core.Xiumi;

/// <summary>
/// LZString（与秀米编辑器使用的上游 1.4.x 逐位兼容）。
/// 移植自 xiumi-key 的 src/lib/lz-string.js——字节级一致是硬要求：
/// 压缩结果差一位，服务端就无法解开 encodedData。
/// </summary>
public static class LzString
{
    private const string KeyStrBase64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    private static readonly Dictionary<char, int> ReverseBase64 = BuildReverse(KeyStrBase64);

    private static Dictionary<char, int> BuildReverse(string alphabet)
    {
        var map = new Dictionary<char, int>(alphabet.Length);
        for (var i = 0; i < alphabet.Length; i++)
        {
            map[alphabet[i]] = i;
        }

        return map;
    }

    public static string CompressToBase64(string input) => Compress(input, 6, v => KeyStrBase64[v]);

    public static string? DecompressFromBase64(string? input)
    {
        if (input is null)
        {
            return string.Empty;
        }

        if (input.Length == 0)
        {
            return null;
        }

        return Decompress(input.Length, 32, index =>
            index < input.Length && ReverseBase64.TryGetValue(input[index], out var value) ? value : 0);
    }

    private static string Compress(string input, int bitsPerChar, Func<int, char> getCharFromInt)
    {
        if (input is null)
        {
            return string.Empty;
        }

        var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
        var toCreate = new HashSet<string>(StringComparer.Ordinal);
        var contextC = string.Empty;
        var contextWc = string.Empty;
        var contextW = string.Empty;
        var enlargeIn = 2;
        var dictSize = 3;
        var numBits = 2;
        var data = new StringBuilder();
        var dataVal = 0;
        var dataPosition = 0;

        void Emit(int value)
        {
            dataVal = (dataVal << 1) | (value & 1);
            if (dataPosition == bitsPerChar - 1)
            {
                dataPosition = 0;
                data.Append(getCharFromInt(dataVal));
                dataVal = 0;
            }
            else
            {
                dataPosition++;
            }
        }

        void WriteBits(int value, int count)
        {
            var v = value;
            for (var i = 0; i < count; i++)
            {
                Emit(v & 1);
                v >>= 1;
            }
        }

        for (var ii = 0; ii < input.Length; ii++)
        {
            contextC = input[ii].ToString();
            if (!dictionary.ContainsKey(contextC))
            {
                dictionary[contextC] = dictSize++;
                toCreate.Add(contextC);
            }

            contextWc = contextW + contextC;
            if (dictionary.ContainsKey(contextWc))
            {
                contextW = contextWc;
            }
            else
            {
                if (toCreate.Contains(contextW))
                {
                    var first = contextW.Length > 0 ? contextW[0] : '\0';
                    if (first < 256)
                    {
                        for (var i = 0; i < numBits; i++)
                        {
                            Emit(0);
                        }

                        WriteBits(first, 8);
                    }
                    else
                    {
                        // 与 JS 一致：numBits 位中首位为 1、其余为 0，随后写 16 位码元
                        Emit(1);
                        for (var i = 1; i < numBits; i++)
                        {
                            Emit(0);
                        }

                        WriteBits(first, 16);
                    }

                    enlargeIn--;
                    if (enlargeIn == 0)
                    {
                        enlargeIn = 1 << numBits;
                        numBits++;
                    }

                    toCreate.Remove(contextW);
                }
                else
                {
                    if (dictionary.TryGetValue(contextW, out var code))
                    {
                        WriteBits(code, numBits);
                    }
                }

                enlargeIn--;
                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                dictionary[contextWc] = dictSize++;
                contextW = contextC;
            }
        }

        if (contextW.Length > 0)
        {
            if (toCreate.Contains(contextW))
            {
                var first = contextW[0];
                if (first < 256)
                {
                    for (var i = 0; i < numBits; i++)
                    {
                        Emit(0);
                    }

                    WriteBits(first, 8);
                }
                else
                {
                    Emit(1);
                    for (var i = 1; i < numBits; i++)
                    {
                        Emit(0);
                    }

                    WriteBits(first, 16);
                }

                enlargeIn--;
                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                toCreate.Remove(contextW);
            }
            else if (dictionary.TryGetValue(contextW, out var code))
            {
                WriteBits(code, numBits);
            }

            enlargeIn--;
            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }

        WriteBits(2, numBits);

        for (;;)
        {
            dataVal <<= 1;
            if (dataPosition == bitsPerChar - 1)
            {
                data.Append(getCharFromInt(dataVal));
                break;
            }

            dataPosition++;
        }

        return data.ToString();
    }

    private static string? Decompress(int length, int resetValue, Func<int, int> getNextValue)
    {
        var dictionary = new List<string?>(capacity: 1024);
        for (var i = 0; i < 3; i++)
        {
            dictionary.Add(null);
        }

        var enlargeIn = 4;
        var dictSize = 4;
        var numBits = 3;
        var entry = string.Empty;
        var result = new StringBuilder();
        var dataVal = getNextValue(0);
        var dataPosition = resetValue;
        var dataIndex = 1;

        int ReadBits(int count)
        {
            var bits = 0;
            var maxPower = 1 << count;
            var power = 1;
            while (power != maxPower)
            {
                var resb = dataVal & dataPosition;
                dataPosition >>= 1;
                if (dataPosition == 0)
                {
                    dataPosition = resetValue;
                    dataVal = getNextValue(dataIndex++);
                }

                if (resb > 0)
                {
                    bits |= power;
                }

                power <<= 1;
            }

            return bits;
        }

        var bits = ReadBits(2);
        string c;
        switch (bits)
        {
            case 0:
                c = ((char)ReadBits(8)).ToString();
                break;
            case 1:
                c = ((char)ReadBits(16)).ToString();
                break;
            case 2:
                return string.Empty;
            default:
                return null;
        }

        dictionary.Add(c);
        var w = c;
        result.Append(c);

        for (;;)
        {
            if (dataIndex > length)
            {
                return string.Empty;
            }

            var code = ReadBits(numBits);
            switch (code)
            {
                case 0:
                    dictionary.Add(((char)ReadBits(8)).ToString());
                    code = dictSize;
                    dictSize++;
                    enlargeIn--;
                    break;
                case 1:
                    dictionary.Add(((char)ReadBits(16)).ToString());
                    code = dictSize;
                    dictSize++;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }

            if (code < dictionary.Count && dictionary[code] is { } dictEntry)
            {
                entry = dictEntry;
            }
            else if (code == dictSize)
            {
                entry = w + (w.Length > 0 ? w[0] : '\0');
            }
            else
            {
                return null;
            }

            result.Append(entry);
            dictionary.Add(w + (entry.Length > 0 ? entry[0] : '\0'));
            dictSize++;
            enlargeIn--;
            w = entry;

            if (enlargeIn == 0)
            {
                enlargeIn = 1 << numBits;
                numBits++;
            }
        }
    }
}