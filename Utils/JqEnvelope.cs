using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;

namespace Dmart.Utils;

// Write a query Response to the HTTP stream, optionally reshaping the
// records[] array through a user-supplied jq filter.
//
// Top-level jq semantics: filter is written per-record (e.g.
// `{shortname, subpath}`). The server wraps as `map(<filter>)`, pipes just
// the records array through `jq -c`, and slots jq's stdout back into
// `records` in the envelope. `status`, `attributes`, `error` are preserved.
//
// Short-circuits (no jq subprocess):
// - `jqFilter` null or whitespace → plain source-gen serialize.
// - response.Status != Success or response.Records is null → plain
//   source-gen serialize. jq only runs against successful result sets.
//
// On jq failure (validation / timeout / runtime) the wire contract is
// preserved: a normal Response.Fail envelope with the appropriate JQ_*
// code. Only successful jq output rides in records[].
//
// Why records-only (not the whole Response envelope): user-authored
// filters are written against a single Record's shape, which is the
// natural mental model. This aligns with the join-sub-query jq path in
// QueryService, which also maps per-record (via `map( [ <filter> ] )` —
// the extra `[ ]` is join-specific for per-base alignment).
public static class JqEnvelope
{
    public static async Task WriteAsync(
        HttpResponse http, Response response, string? jqFilter, int timeoutSec, CancellationToken ct)
    {
        http.ContentType = "application/json; charset=utf-8";

        if (string.IsNullOrWhiteSpace(jqFilter)
            || response.Status != Status.Success
            || response.Records is null)
        {
            await JsonSerializer.SerializeAsync(http.Body, response, DmartJsonContext.Default.Response, ct);
            return;
        }

        var inputBytes = SerializeRecordsAsArray(response.Records);
        var jq = await JqRunner.RunRawAsync($"map({jqFilter})", inputBytes, timeoutSec, ct);

        if (jq.Failure != JqRunner.FailureKind.None)
        {
            var fail = JqRunner.ToFailureResponse(jq.Failure, jq.Stderr);
            await JsonSerializer.SerializeAsync(http.Body, fail, DmartJsonContext.Default.Response, ct);
            return;
        }

        await WriteEnvelopeAsync(http, response, jq.StdoutBytes ?? Array.Empty<byte>(), ct);
    }

    // Build the full Response envelope with raw jq output as the `records`
    // field value. `status`, `attributes`, `error` go through the source-gen
    // serializers; `records` is written via WriteRawValue to avoid a
    // parse+reserialize round-trip.
    private static async Task WriteEnvelopeAsync(
        HttpResponse http, Response response, byte[] jqStdout, CancellationToken ct)
    {
        await using var writer = new Utf8JsonWriter(http.Body);
        writer.WriteStartObject();

        writer.WritePropertyName("status");
        JsonSerializer.Serialize(writer, response.Status, DmartJsonContext.Default.Status);

        if (response.Error is not null)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, response.Error, DmartJsonContext.Default.Error);
        }

        // jq -c `map(...)` emits a single JSON array on one line with a
        // trailing newline. Trim whitespace and write the array bytes raw.
        writer.WritePropertyName("records");
        var trimmed = TrimTrailingWhitespace(jqStdout);
        if (trimmed.Length == 0)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(trimmed, skipInputValidation: false);

        if (response.Attributes is not null)
        {
            writer.WritePropertyName("attributes");
            JsonSerializer.Serialize(writer, response.Attributes,
                DmartJsonContext.Default.DictionaryStringObject);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    // Serialize records as a JSON array using the source-gen Record serializer,
    // so the byte-for-byte shape fed into jq matches dmart's wire format.
    private static byte[] SerializeRecordsAsArray(List<Record> records)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var rec in records)
                JsonSerializer.Serialize(writer, rec, DmartJsonContext.Default.Record);
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    private static ReadOnlySpan<byte> TrimTrailingWhitespace(byte[] bytes)
    {
        var end = bytes.Length;
        while (end > 0)
        {
            var b = bytes[end - 1];
            if (b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t')
                end--;
            else break;
        }
        return new ReadOnlySpan<byte>(bytes, 0, end);
    }
}
