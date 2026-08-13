using System.Text;

namespace Dmart.DataAdapters.Parquet;

// The reading half of the Thrift compact subset. Mirrors ThriftCompactWriter,
// with one capability the writer never needed: skipping fields it does not
// understand.
//
// That matters more than it sounds. Every real Parquet writer emits fields we
// do not — statistics, key-value metadata, column orders, logical types — and a
// reader that cannot step over an unknown field cannot read anyone else's
// files. Since cross-verification against an independent writer is the entire
// point (docs/parquet-export-design.md §2.3), skipping is load-bearing, not
// defensive padding.
internal sealed class ThriftCompactReader(Stream input)
{
    internal const byte TStop = 0;
    internal const byte TBoolTrue = 1;
    internal const byte TBoolFalse = 2;
    internal const byte TByte = 3;
    internal const byte TI16 = 4;
    internal const byte TI32 = 5;
    internal const byte TI64 = 6;
    internal const byte TDouble = 7;
    internal const byte TBinary = 8;
    internal const byte TList = 9;
    internal const byte TSet = 10;
    internal const byte TMap = 11;
    internal const byte TStruct = 12;

    private readonly Stream _in = input;
    private short _lastFieldId;
    private readonly Stack<short> _fieldIdStack = new();

    // ---- primitives ----

    private byte ReadByte()
    {
        var b = _in.ReadByte();
        if (b < 0) throw new InvalidDataException("unexpected end of Thrift data");
        return (byte)b;
    }

    private ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("varint is too long to be valid");
        }
    }

    private static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    public long ReadI64() => UnZigZag(ReadVarint());
    public int ReadI32() => (int)UnZigZag(ReadVarint());

    public string ReadString()
    {
        var bytes = ReadBinary();
        return Encoding.UTF8.GetString(bytes);
    }

    public byte[] ReadBinary()
    {
        var len = (int)ReadVarint();
        var buf = new byte[len];
        _in.ReadExactly(buf);
        return buf;
    }

    // ---- structs and fields ----

    public void StructBegin()
    {
        _fieldIdStack.Push(_lastFieldId);
        _lastFieldId = 0;
    }

    public void StructEnd() => _lastFieldId = _fieldIdStack.Pop();

    /// <summary>
    /// Reads the next field header. Returns false at the STOP byte, after which
    /// the caller must call <see cref="StructEnd"/>.
    /// </summary>
    public bool TryReadFieldHeader(out short fieldId, out byte type)
    {
        var b = ReadByte();
        if (b == TStop) { fieldId = 0; type = TStop; return false; }

        type = (byte)(b & 0x0F);
        var delta = (b >> 4) & 0x0F;
        // A zero delta means the id did not fit in the nibble and follows as a
        // zigzag varint — not that the id is the same as the last one.
        fieldId = delta == 0 ? (short)UnZigZag(ReadVarint()) : (short)(_lastFieldId + delta);
        _lastFieldId = fieldId;
        return true;
    }

    /// <summary>Reads a list header, returning the element count.</summary>
    public int ReadListHeader(out byte elementType)
    {
        var b = ReadByte();
        elementType = (byte)(b & 0x0F);
        var size = (b >> 4) & 0x0F;
        return size == 0x0F ? (int)ReadVarint() : size;
    }

    /// <summary>
    /// Steps over a value of the given type, including nested containers.
    /// </summary>
    public void Skip(byte type)
    {
        switch (type)
        {
            case TBoolTrue or TBoolFalse:
                break;                                  // value is in the type nibble
            case TByte:
                ReadByte();
                break;
            case TI16 or TI32 or TI64:
                ReadVarint();
                break;
            case TDouble:
                Span<byte> eight = stackalloc byte[8];
                _in.ReadExactly(eight);
                break;
            case TBinary:
                ReadBinary();
                break;
            case TList or TSet:
            {
                var count = ReadListHeader(out var elem);
                for (var i = 0; i < count; i++) Skip(elem);
                break;
            }
            case TMap:
            {
                var count = (int)ReadVarint();
                if (count > 0)
                {
                    var kv = ReadByte();
                    var keyType = (byte)((kv >> 4) & 0x0F);
                    var valType = (byte)(kv & 0x0F);
                    for (var i = 0; i < count; i++) { Skip(keyType); Skip(valType); }
                }
                break;
            }
            case TStruct:
                StructBegin();
                while (TryReadFieldHeader(out _, out var ft)) Skip(ft);
                StructEnd();
                break;
            default:
                throw new InvalidDataException($"unknown Thrift type code {type}");
        }
    }
}
