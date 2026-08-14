namespace Dmart.DataAdapters.Parquet;

// Decodes Parquet's RLE/bit-packed hybrid, used here for definition levels.
//
// The writer deliberately emits only RLE runs (see RleEncoder), but the decoder
// MUST handle both forms: any other writer picks whichever is denser, and
// pyarrow routinely emits bit-packed runs for alternating levels. Supporting
// only what we produce would make the reader unable to read anything but our
// own files, which would defeat the cross-verification the encoder is being
// checked with.
internal static class RleDecoder
{
    /// <summary>
    /// Decodes exactly <paramref name="count"/> levels from a section that
    /// begins with a 4-byte little-endian byte length, as a v1 data page's
    /// definition levels do.
    /// </summary>
    /// <returns>The levels, and how many bytes of <paramref name="data"/> they occupied.</returns>
    public static (int[] Levels, int BytesConsumed) DecodeLevelsWithLengthPrefix(
        ReadOnlySpan<byte> data, int count, int bitWidth)
    {
        if (data.Length < 4) throw new InvalidDataException("level section is too short for its length prefix");
        var sectionLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
        if (sectionLength < 0 || 4 + sectionLength > data.Length)
            throw new InvalidDataException($"level section claims {sectionLength} bytes, page has {data.Length - 4}");

        var levels = Decode(data.Slice(4, sectionLength), count, bitWidth);
        return (levels, 4 + sectionLength);
    }

    /// <summary>Decodes <paramref name="count"/> values from a hybrid-encoded run sequence.</summary>
    public static int[] Decode(ReadOnlySpan<byte> data, int count, int bitWidth)
    {
        var result = new int[count];
        var produced = 0;
        var pos = 0;

        while (produced < count)
        {
            if (pos >= data.Length)
                throw new InvalidDataException(
                    $"level data ran out after {produced} of {count} values");

            var header = ReadVarint(data, ref pos);

            if ((header & 1) == 0)
            {
                // RLE run: one value repeated. The value is stored in the
                // smallest whole number of bytes that holds bitWidth bits.
                var runLength = (int)(header >> 1);
                var value = ReadFixedWidth(data, ref pos, (bitWidth + 7) / 8);

                // A corrupt run length must not write past the caller's buffer;
                // clamping instead would silently truncate the column.
                if (produced + runLength > count)
                    throw new InvalidDataException(
                        $"RLE run of {runLength} overruns the remaining {count - produced} values");

                result.AsSpan(produced, runLength).Fill(value);
                produced += runLength;
            }
            else
            {
                // Bit-packed run: (header >> 1) groups of EIGHT values each.
                // Reading it as a count of values rather than of groups is the
                // classic misread — it decodes and returns an eighth of the data.
                var groups = (int)(header >> 1);
                var values = groups * 8;

                for (var g = 0; g < groups; g++)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var index = g * 8 + i;
                        // The final group is padded to eight; the padding is
                        // real data in the stream but not part of the column.
                        if (produced + index >= count) continue;
                        result[produced + index] = ReadBitPacked(data, pos, index, bitWidth);
                    }
                }

                pos += (values * bitWidth + 7) / 8;
                produced += Math.Min(values, count - produced);
            }
        }

        return result;
    }

    // Values are packed LSB-first and may straddle byte boundaries.
    private static int ReadBitPacked(ReadOnlySpan<byte> data, int basePos, int index, int bitWidth)
    {
        var value = 0;
        for (var bit = 0; bit < bitWidth; bit++)
        {
            var absolute = index * bitWidth + bit;
            var byteIndex = basePos + (absolute / 8);
            if (byteIndex >= data.Length)
                throw new InvalidDataException("bit-packed run extends past the level data");
            if ((data[byteIndex] & (1 << (absolute % 8))) != 0)
                value |= 1 << bit;
        }
        return value;
    }

    private static int ReadFixedWidth(ReadOnlySpan<byte> data, ref int pos, int byteCount)
    {
        var value = 0;
        for (var i = 0; i < byteCount; i++)
        {
            if (pos >= data.Length) throw new InvalidDataException("RLE run value is truncated");
            value |= data[pos++] << (i * 8);
        }
        return value;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= data.Length) throw new InvalidDataException("truncated RLE run header");
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("RLE run header varint is too long");
        }
    }
}
