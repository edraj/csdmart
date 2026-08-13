namespace Dmart.DataAdapters.Parquet;

// Parquet's RLE / bit-packed hybrid encoding, used here for definition levels.
//
// Definition levels are how Parquet says "this value is null" without storing
// a placeholder: for a flat optional column the level is 1 when a value is
// present and 0 when it is not, and the values section then contains ONLY the
// present ones. Get this wrong and the file is not corrupt — it decodes, with
// values shifted into the wrong rows. That is why the cross-reader test
// asserts null POSITIONS rather than just null counts.
//
// The format is a sequence of runs, each introduced by a varint header:
//   header & 1 == 0  -> RLE run:        (header >> 1) repeats of one value
//   header & 1 == 1  -> bit-packed run: (header >> 1) groups of 8 values
//
// Only RLE runs are emitted. Bit-packed runs are denser for rapidly
// alternating levels, but both are valid and a reader must accept either;
// choosing the simpler one removes a whole class of packing bug from a
// component whose failures are silent. Worth revisiting only if measurement
// says level bytes matter, which for 1-bit levels they are unlikely to.
internal static class RleEncoder
{
    /// <summary>
    /// RLE-encodes <paramref name="levels"/> at the given bit width, prefixed
    /// with the 4-byte little-endian length a v1 data page requires.
    /// </summary>
    public static byte[] EncodeLevelsWithLengthPrefix(IReadOnlyList<int> levels, int bitWidth)
    {
        var body = Encode(levels, bitWidth);
        var result = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)body.Length);
        body.CopyTo(result, 4);
        return result;
    }

    public static byte[] Encode(IReadOnlyList<int> levels, int bitWidth)
    {
        using var ms = new MemoryStream();
        // Values in an RLE run occupy ceil(bitWidth/8) bytes, little-endian.
        var valueBytes = (bitWidth + 7) / 8;

        var i = 0;
        while (i < levels.Count)
        {
            var value = levels[i];
            var runLength = 1;
            while (i + runLength < levels.Count && levels[i + runLength] == value) runLength++;

            WriteVarint(ms, (ulong)runLength << 1);        // LSB 0 => RLE run
            for (var b = 0; b < valueBytes; b++)
                ms.WriteByte((byte)((value >> (8 * b)) & 0xFF));

            i += runLength;
        }
        return ms.ToArray();
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (true)
        {
            if ((value & ~0x7FUL) == 0) { s.WriteByte((byte)value); return; }
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
    }
}
