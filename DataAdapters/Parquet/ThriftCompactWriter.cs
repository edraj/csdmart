using System.Buffers.Binary;
using System.Text;

namespace Dmart.DataAdapters.Parquet;

// The subset of Thrift's compact protocol that Parquet's metadata needs.
//
// Parquet stores its footer and every page header as Thrift compact structs,
// so this is the foundation the whole encoder sits on: get a field id or a
// varint wrong and the file is unreadable, usually with an error that names
// neither. There is no reflection here and no generated code — the structures
// are written out by hand in ParquetMetadata, which is what keeps this
// AOT-clean where a Thrift library would not be.
//
// Compact protocol, in the parts used here:
//   * integers are ULEB128 varints; signed ones are zigzagged first
//   * a field header packs (delta from the previous field id, type) into one
//     byte when the delta fits in 4 bits, otherwise emits type then a
//     zigzagged field id
//   * structs end with a STOP byte, and nesting saves/restores the field id
//     the deltas are relative to
internal sealed class ThriftCompactWriter(Stream output)
{
    // Compact-protocol type codes. BOOL is special: at field level the VALUE
    // is encoded in the type nibble, so there is one code for true and one for
    // false and no payload follows.
    private const byte TBoolTrue = 1;
    private const byte TBoolFalse = 2;
    private const byte TI32 = 5;
    private const byte TI64 = 6;
    private const byte TBinary = 8;
    private const byte TList = 9;
    private const byte TStruct = 12;

    private readonly Stream _out = output;
    private short _lastFieldId;
    private readonly Stack<short> _fieldIdStack = new();

    // ---- primitives ----

    private void WriteByte(byte b) => _out.WriteByte(b);

    private void WriteVarint(ulong value)
    {
        Span<byte> buf = stackalloc byte[10];
        var i = 0;
        while (true)
        {
            if ((value & ~0x7FUL) == 0) { buf[i++] = (byte)value; break; }
            buf[i++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        _out.Write(buf[..i]);
    }

    // Zigzag: maps signed to unsigned so small negatives stay short.
    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    private static ulong ZigZag(int v) => (ulong)((v << 1) ^ (v >> 31));

    // ---- field headers ----

    private void WriteFieldHeader(byte type, short fieldId)
    {
        var delta = fieldId - _lastFieldId;
        if (delta > 0 && delta <= 15)
        {
            WriteByte((byte)((delta << 4) | type));
        }
        else
        {
            // Delta does not fit (or went backwards): long form.
            WriteByte(type);
            WriteVarint(ZigZag((int)fieldId));
        }
        _lastFieldId = fieldId;
    }

    public void WriteI32Field(short id, int value)
    {
        WriteFieldHeader(TI32, id);
        WriteVarint(ZigZag(value));
    }

    public void WriteI64Field(short id, long value)
    {
        WriteFieldHeader(TI64, id);
        WriteVarint(ZigZag(value));
    }

    public void WriteBoolField(short id, bool value)
        // No payload — the value rides in the type nibble.
        => WriteFieldHeader(value ? TBoolTrue : TBoolFalse, id);

    public void WriteStringField(short id, string value)
    {
        WriteFieldHeader(TBinary, id);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint((ulong)bytes.Length);
        _out.Write(bytes);
    }

    // ---- containers ----

    public void WriteStructFieldBegin(short id)
    {
        WriteFieldHeader(TStruct, id);
        StructBegin();
    }

    // A nested struct restarts field-id deltas from 0 and the enclosing
    // struct resumes its own numbering afterwards.
    public void StructBegin()
    {
        _fieldIdStack.Push(_lastFieldId);
        _lastFieldId = 0;
    }

    public void StructEnd()
    {
        WriteByte(0x00);   // STOP
        _lastFieldId = _fieldIdStack.Pop();
    }

    public void WriteListFieldBegin(short id, byte elementType, int count)
    {
        WriteFieldHeader(TList, id);
        WriteListHeader(elementType, count);
    }

    private void WriteListHeader(byte elementType, int count)
    {
        if (count < 15)
        {
            WriteByte((byte)((count << 4) | elementType));
        }
        else
        {
            WriteByte((byte)(0xF0 | elementType));
            WriteVarint((ulong)count);
        }
    }

    // Element writers for lists — no field header, just the value.
    public void WriteListI32(int value) => WriteVarint(ZigZag(value));

    public void WriteListString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint((ulong)bytes.Length);
        _out.Write(bytes);
    }

    // Element type codes, for callers building lists.
    public const byte ElemI32 = TI32;
    public const byte ElemI64 = TI64;
    public const byte ElemBinary = TBinary;
    public const byte ElemStruct = TStruct;
}
