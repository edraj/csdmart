// AOT-ready source-generated JSON for dmart.Client.
//
// Only compiled on net8.0+ where System.Text.Json's JsonSerializerContext
// exists. The netstandard2.1 leg continues to use reflection-based
// JsonSerializerOptions (DefaultJsonOptions) — that leg is not AOT-safe,
// but .NET runtimes that support AOT (net6+) don't use the netstandard
// path anyway, so this split gives modern consumers trim-safe plumbing
// without breaking older ones.
//
// The 5 reflection callsites at DmartClient.cs:102,237,256 and
// DmartClient.Extra.cs:227,278 route through this context on net8.0+.
// Wire convention matches the server (SnakeCaseLower, omit-when-null).
#if NET8_0_OR_GREATER

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Client.Json;

// Same integer tolerance as DmartClient.DefaultJsonOptions — the two legs must
// not disagree about what they can read, or a payload would parse on
// netstandard2.1 and fail on net8.0+. See Json/IntegralNumberConverters.cs.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(IntegralInt32Converter), typeof(IntegralInt64Converter) })]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Record))]
[JsonSerializable(typeof(Query))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(ResourceType))]
[JsonSerializable(typeof(QueryType))]
// Typed parity facade hydrates these from /managed/entry responses and
// query records. Keeping them source-gen-registered means the typed CRUD
// surface (LoadAsync, FetchSpaceAsync, LoadUserMetaAsync, QueryEntriesAsync)
// stays AOT-safe.
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Space))]
[JsonSerializable(typeof(User))]
// Dictionary<string, object?> — used for ad-hoc request bodies (login, otp,
// reset, etc.). Nullability annotations are erased at runtime, so the
// canonical typeof() form is Dictionary<string, object>.
[JsonSerializable(typeof(Dictionary<string, object>))]
// Every CLR type an attribute bag can carry MUST be registered here.
// Record.Attributes / Request.Attributes are Dictionary<string, object>, so
// System.Text.Json resolves each value by its RUNTIME type; a type this context
// doesn't know throws NotSupportedException at serialize time, before the request
// is ever sent. The netstandard2.1 leg is reflection-based and never had the
// problem, so a gap here only breaks modern (net8.0+) consumers — which is how
// `decimal` money fields went unnoticed until an integrator hit them.
//
// The list below is the closed set of JSON-representable scalars plus the
// collection shapes callers reach for. A consumer POCO still cannot go in an
// attribute bag: hand it over as a JsonElement (JsonSerializer.SerializeToElement
// against your own context), which is inherent to staying trim/AOT-safe.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(decimal[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<long>))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(List<decimal>))]
[JsonSerializable(typeof(List<bool>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class DmartClientJsonContext : JsonSerializerContext;

#endif
