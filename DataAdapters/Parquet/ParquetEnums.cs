namespace Dmart.DataAdapters.Parquet;

// Wire values from parquet.thrift. These are format constants, not our
// choices — changing one silently produces a file other readers misinterpret,
// so they are written out explicitly rather than derived.

internal enum ParquetType
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    Int96 = 3,
    Float = 4,
    Double = 5,
    ByteArray = 6,
    FixedLenByteArray = 7,
}

internal enum FieldRepetitionType
{
    Required = 0,
    Optional = 1,
    Repeated = 2,
}

internal enum ParquetEncoding
{
    Plain = 0,
    Rle = 3,
}

internal enum CompressionCodec
{
    Uncompressed = 0,
    Zstd = 6,
}

internal enum PageType
{
    DataPage = 0,
    DictionaryPage = 2,
    DataPageV2 = 3,
}

// Legacy annotation, still what most readers key off for strings and
// timestamps. The newer LogicalType union is optional; ConvertedType alone is
// understood everywhere, so the skeleton uses it and adds LogicalType later
// only if a reader needs it.
internal enum ConvertedType
{
    Utf8 = 0,
    TimestampMillis = 9,
    TimestampMicros = 10,
}
