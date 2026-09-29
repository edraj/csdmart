using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dmart.Cli;

// One part of a CSV upload that dmart's time limit cut off: rows FromRow to
// ResumeRow - 1 were handled, and the upload continues from ResumeRow.
public sealed record CsvUploadPart(int FromRow, int ResumeRow, int Inserted, int Failed);

// HTTP client for dmart REST API — mirrors Python cli.py's DMart class.
// All JSON request bodies are built as literal strings for AOT compatibility
// (no reflection-based serialization).
public sealed class DmartClient : IDisposable
{
    // A CSV upload is not an interactive call: dmart itself stops it at
    // REQUEST_TIMEOUT and answers with the row to resume from, so the client
    // must outwait that answer rather than cut it off at 30 s. This only
    // guards against a connection that died without closing.
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly HttpClient _http;
    private readonly HttpClient _uploadHttp;
    private readonly CliSettings _settings;
    private string? _token;

    public List<string> SpaceNames { get; private set; } = new();
    public string CurrentSpace { get; set; }
    public string CurrentSubpath { get; set; } = "/";
    public List<JsonElement> CurrentEntries { get; private set; } = new();

    // Wall-clock millis the last LoginAsync took — surfaced in the banner so
    // operators see the round-trip latency to the configured server up front.
    public long LastLoginLatencyMs { get; private set; }

    public DmartClient(CliSettings settings) : this(settings, new HttpClientHandler(), ownsHandler: true) { }

    // Both clients share `handler` (connections, cookies); tests pass the
    // in-memory server's. A handler that came from outside belongs to whoever
    // built it: two DmartClients can share one, and a TestServer handler can
    // outlive the client that borrowed it, so Dispose leaves it alone. That is
    // also what `disposeHandler: false` below already promised.
    internal DmartClient(CliSettings settings, HttpMessageHandler handler)
        : this(settings, handler, ownsHandler: false) { }

    private DmartClient(CliSettings settings, HttpMessageHandler handler, bool ownsHandler)
    {
        _settings = settings;
        CurrentSpace = settings.DefaultSpace;
        _handler = handler;
        _ownsHandler = ownsHandler;
        // Default HttpClient.Timeout is 100s — too long for an interactive
        // REPL where a hung server should surface within seconds, not after
        // the user has wandered off.
        _http = NewHttpClient(TimeSpan.FromSeconds(30));
        _uploadHttp = NewHttpClient(UploadTimeout);
    }

    private HttpClient NewHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(_settings.Url.TrimEnd('/')),
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    // ---- Auth ----

    public async Task<(bool Ok, string? Error)> LoginAsync()
    {
        HttpResponseMessage resp;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var body = Json($"{{\"shortname\":\"{Esc(_settings.Shortname)}\",\"password\":\"{Esc(_settings.Password)}\"}}");
            resp = await _http.PostAsync("/user/login", body);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Cannot connect to {_settings.Url}: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, $"Timeout connecting to {_settings.Url} after {_http.Timeout.TotalSeconds:0}s");
        }
        finally { sw.Stop(); LastLoginLatencyMs = sw.ElapsedMilliseconds; }
        var json = await ParseAsync(resp);
        if (json.TryGetProperty("status", out var st) && st.GetString() == "success")
        {
            _token = json.GetProperty("records")[0].GetProperty("attributes")
                .GetProperty("access_token").GetString();
            var auth = new AuthenticationHeaderValue("Bearer", _token);
            _http.DefaultRequestHeaders.Authorization = auth;
            _uploadHttp.DefaultRequestHeaders.Authorization = auth;
            return (true, null);
        }
        var msg = json.TryGetProperty("error", out var err) ? err.GetProperty("message").GetString() : "login failed";
        return (false, msg);
    }

    // Wrap an HTTP send so a 401 transparently re-LoginAsync's and retries
    // once. Mid-session JWT expiry would otherwise turn every command into a
    // silent failure with no remediation other than restarting the REPL.
    private async Task<HttpResponseMessage> SendWithRefreshAsync(Func<Task<HttpResponseMessage>> send)
    {
        var resp = await send();
        if (resp.StatusCode != System.Net.HttpStatusCode.Unauthorized || _token is null)
            return resp;
        // Drop the response body so the connection returns to the pool.
        resp.Dispose();
        var (ok, _) = await LoginAsync();
        if (!ok) return await send(); // best-effort — caller will see the 401
        return await send();
    }

    // ---- Spaces ----

    public async Task<List<string>> FetchSpacesAsync(bool force = false)
    {
        if (!force && SpaceNames.Count > 0) return SpaceNames;
        try
        {
            var resp = await PostQueryAsync(
                $"{{\"type\":\"spaces\",\"space_name\":\"management\",\"subpath\":\"/\",\"limit\":100}}");
            if (resp.TryGetProperty("records", out var recs))
                SpaceNames = recs.EnumerateArray().Select(r => r.GetProperty("shortname").GetString()!).ToList();
        }
        catch { /* query failed — use whatever we have */ }
        if (!SpaceNames.Contains(CurrentSpace))
            SpaceNames.Insert(0, CurrentSpace);
        return SpaceNames;
    }

    // ---- Navigation ----

    public async Task ListAsync()
    {
        var sub = CurrentSubpath.Replace("//", "/");
        var resp = await PostQueryAsync(
            $"{{\"space_name\":\"{Esc(CurrentSpace)}\",\"type\":\"subpath\",\"subpath\":\"{Esc(sub)}\",\"retrieve_json_payload\":true,\"limit\":100}}");
        CurrentEntries.Clear();
        if (resp.TryGetProperty("records", out var recs))
            CurrentEntries = recs.EnumerateArray().ToList();
    }

    // ---- CRUD ----

    public Task<JsonElement> CreateFolderAsync(string shortname)
        => ManagedRequestAsync("create", RecordJson("folder", CurrentSubpath, shortname, "\"is_active\":true"));

    public Task<JsonElement> CreateEntryAsync(string shortname, string resourceType)
        => ManagedRequestAsync("create", RecordJson(resourceType, CurrentSubpath, shortname, "\"is_active\":true"));

    public Task<JsonElement> DeleteAsync(string shortname, string resourceType)
        => ManagedRequestAsync("delete", RecordJson(resourceType, CurrentSubpath, shortname, null));

    public async Task<JsonElement> MoveAsync(string resourceType, string srcSubpath, string srcShortname,
        string destSubpath, string destShortname)
    {
        var attrs = $"\"src_subpath\":\"{Esc(srcSubpath)}\",\"src_shortname\":\"{Esc(srcShortname)}\",\"dest_subpath\":\"{Esc(destSubpath)}\",\"dest_shortname\":\"{Esc(destShortname)}\"";
        return await ManagedRequestAsync("move",
            RecordJson(resourceType, CurrentSubpath, srcShortname, attrs));
    }

    public async Task<JsonElement> ManageSpaceAsync(string spaceName, string requestType)
    {
        var json = $"{{\"space_name\":\"{Esc(spaceName)}\",\"request_type\":\"{Esc(requestType)}\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{Esc(spaceName)}\",\"attributes\":{{}}}}]}}";
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/request", Json(json)));
        var result = await ParseAsync(resp);
        await FetchSpacesAsync(force: true);
        return result;
    }

    public async Task<JsonElement> ProgressTicketAsync(string subpath, string shortname, string action)
    {
        var resp = await SendWithRefreshAsync(() =>
            _http.PutAsync($"/managed/progress-ticket/{CurrentSpace}/{subpath}/{shortname}/{action}", null));
        return await ParseAsync(resp);
    }

    // Lightweight server probe — used by the interactive shell's `version`
    // command to show server-side info. /info/manifest requires super_admin
    // (GlobalAdminFilter); a non-admin caller gets a 401 here, which we
    // swallow to null so `version` just omits the server-side fields
    // instead of failing the command.
    public async Task<JsonElement?> ManifestAsync()
    {
        try
        {
            var resp = await SendWithRefreshAsync(() => _http.GetAsync("/info/manifest"));
            if (!resp.IsSuccessStatusCode) return null;
            return await ParseAsync(resp);
        }
        catch { return null; }
    }

    // Recursive search across the current space — wraps /managed/query with
    // type=search. Pattern is forwarded verbatim (server uses Postgres FTS).
    public async Task<JsonElement> FindAsync(string pattern, string? subpath = null,
        string? resourceType = null, int limit = 50)
    {
        var sub = subpath ?? "/";
        var rtFilter = resourceType is null ? ""
            : $",\"filter_types\":[\"{Esc(resourceType)}\"]";
        var query = $"{{\"type\":\"search\",\"space_name\":\"{Esc(CurrentSpace)}\",\"subpath\":\"{Esc(sub)}\",\"search\":\"{Esc(pattern)}\",\"retrieve_json_payload\":true,\"limit\":{limit}{rtFilter}}}";
        return await PostQueryAsync(query);
    }

    // ---- Upload ----

    public Task<JsonElement> UploadSchemaAsync(string shortname, string filePath)
    {
        var recordJson = $"{{\"resource_type\":\"schema\",\"subpath\":\"schema\",\"shortname\":\"{Esc(shortname)}\",\"attributes\":{{\"schema_shortname\":\"meta_schema\",\"is_active\":true}}}}";
        return UploadWithPayloadAsync(recordJson, filePath);
    }

    // Uploads a CSV to the end, in as many requests as it takes: when dmart's
    // REQUEST_TIMEOUT stops the import part-way, its 504 names the row to
    // resume from (error.info[0].resume_row) and the rest is sent from there.
    // Returns one summary in the shape of a single upload's answer, with the
    // totals and every failed row across parts. On a failure it is "failed",
    // with the error and the `resume_row` to pick up from (--start-row).
    public async Task<JsonElement> UploadCsvToEndAsync(
        string resourceType, string subpath, string schemaShortname, string filePath,
        bool isUpdate = false, int startRow = 1, Action<CsvUploadPart>? onPart = null)
    {
        var inserted = 0;
        var failed = new List<JsonElement>();
        while (true)
        {
            int status;
            JsonElement body;
            try
            {
                (status, body) = await UploadCsvPartAsync(
                    resourceType, subpath, schemaShortname, filePath, isUpdate, startRow);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return CsvSummary(inserted, failed, startRow, MessageJson(ex.Message));
            }

            if (body.TryGetProperty("status", out var st) && st.GetString() == "success")
            {
                if (body.TryGetProperty("attributes", out var attrs)) Absorb(attrs, ref inserted, failed);
                return CsvSummary(inserted, failed, resumeRow: null, error: null);
            }

            // A part the time limit cut off reports its progress in error.info[0].
            if (status == 504
                && body.TryGetProperty("error", out var err)
                && err.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array
                && info.GetArrayLength() > 0 && info[0].TryGetProperty("inserted", out _))
            {
                var progress = info[0];
                var insertedBefore = inserted;
                var failedBefore = failed.Count;
                Absorb(progress, ref inserted, failed);
                // No resume_row: every row was processed, only the answer ran late.
                if (!progress.TryGetProperty("resume_row", out var rr))
                    return CsvSummary(inserted, failed, resumeRow: null, error: null);
                var resumeRow = rr.GetInt32();
                // Not one row fit in the limit: sending it again would loop forever.
                if (resumeRow <= startRow)
                    return CsvSummary(inserted, failed, startRow, err);
                onPart?.Invoke(new CsvUploadPart(
                    startRow, resumeRow, inserted - insertedBefore, failed.Count - failedBefore));
                startRow = resumeRow;
                continue;
            }

            if (body.TryGetProperty("error", out var error))
            {
                // A terminal failure can still have committed rows in this part:
                // the 100,000-row cap is only reached once the rows below it are
                // saved, so a resumed import that crosses it has written tens of
                // thousands of entries. That progress rides in the same
                // error.info[0] block the timeout answer uses.
                if (error.TryGetProperty("info", out var errInfo) && errInfo.ValueKind == JsonValueKind.Array
                    && errInfo.GetArrayLength() > 0 && errInfo[0].TryGetProperty("inserted", out _))
                    Absorb(errInfo[0], ref inserted, failed);
                return CsvSummary(inserted, failed, startRow, error);
            }
            return CsvSummary(inserted, failed, startRow,
                MessageJson($"unexpected answer (HTTP {status}): {Truncate(body.ToString(), 200)}"));
        }
    }

    private async Task<(int Status, JsonElement Body)> UploadCsvPartAsync(
        string resourceType, string subpath, string schemaShortname, string filePath, bool isUpdate, int startRow)
    {
        // Only the rows this part imports are uploaded: the header, then the
        // file from the byte offset row `startRow` begins at. Re-sending the
        // whole file on every part made a resumed import transfer it once per
        // part — and the server re-parse and discard the earlier rows each time.
        // `first_row` (not `start_row`) tells it the body starts AT that row, so
        // the rows it reports stay the operator's own row numbers.
        var slice = startRow > 1 ? await CsvSliceAsync(filePath, startRow) : null;
        var query = new List<string>();
        if (isUpdate) query.Add("is_update=true");
        // Falling back to the whole file (slice is null) means the scan found no
        // such row — or no header line at all — so the server must skip to that
        // row rather than renumber a body that does not start there.
        if (startRow > 1) query.Add(slice is null ? $"start_row={startRow}" : $"first_row={startRow}");
        var path = $"/managed/resources_from_csv/{resourceType}/{CurrentSpace}/{subpath}/{schemaShortname}"
                   + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        // A fresh form per attempt: the retry after a token refresh would
        // otherwise re-send a file stream the first attempt read to the end.
        var resp = await SendWithRefreshAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            HttpContent content = slice is { } s
                ? new CsvPartContent(s.Header, filePath, s.Offset)
                : new StreamContent(File.OpenRead(filePath));
            form.Add(content, "resources_file", Path.GetFileName(filePath));
            return await _uploadHttp.PostAsync(path, form);
        });
        return ((int)resp.StatusCode, await ParseAsync(resp));
    }

    // A header line longer than this is not a header line. Bounds the buffer the
    // scan below builds before it has seen a single line break.
    private const int MaxCsvHeaderBytes = 8 * 1024 * 1024;

    // The header line's bytes (its line break included) and the byte offset at
    // which file row `startRow` begins, or null when the file has no such row —
    // or no header line at all — in which case the caller uploads the whole file
    // and lets the server skip.
    //
    // Rows are lines: CsvService reads the upload with ReadLineAsync, so the two
    // must agree on what ends one. That is "\n", "\r\n" or a lone "\r", exactly
    // as StreamReader sees it. Scanning raw bytes is safe because none of those
    // can occur inside a UTF-8 multi-byte sequence.
    private static async Task<(byte[] Header, long Offset)?> CsvSliceAsync(string filePath, int startRow)
    {
        await using var fs = File.OpenRead(filePath);
        using var head = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long consumed = 0;
        var lines = 0;
        var pendingCr = false;
        byte[]? header = null;
        int read;
        while ((read = await fs.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var at = consumed + i;
                if (lines == 0)
                {
                    if (head.Length >= MaxCsvHeaderBytes) return null;
                    head.WriteByte(buffer[i]);
                }
                // A "\r" already seen and this byte is not the "\n" that would
                // have joined it: the line ended before this byte.
                if (pendingCr && buffer[i] != (byte)'\n' && Ends(at) is { } early) return early;
                pendingCr = false;
                if (buffer[i] == (byte)'\n')
                {
                    if (Ends(at + 1) is { } here) return here;
                }
                else if (buffer[i] == (byte)'\r')
                {
                    pendingCr = true;
                }
            }
            consumed += read;
        }
        // A trailing lone "\r" still ends a line, at end of file — but there is
        // nothing after it to send.
        return null;

        // Records a line ending at byte `offset` (the first byte of the next
        // line) and returns the slice once `startRow` is reached. Line 1 is the
        // header, so data row N begins after the Nth line break.
        (byte[] Header, long Offset)? Ends(long offset)
        {
            lines++;
            if (lines == 1)
            {
                var buffered = head.ToArray();
                // `head` holds the current byte too; the header line stops at
                // the break, which for a lone "\r" is one byte back.
                header = buffered.Length == offset ? buffered : buffered[..(int)offset];
            }
            return lines == startRow ? (header!, offset) : null;
        }
    }

    // Streams a CSV's header line followed by the file from `offset` — the body
    // of one resumed upload part. Built fresh per attempt (SendWithRefreshAsync
    // re-runs its factory after a token refresh), so it owns the handle it
    // opens and MultipartFormDataContent disposes it with the form.
    private sealed class CsvPartContent : HttpContent
    {
        private readonly byte[] _header;
        private readonly FileStream _file;

        public CsvPartContent(byte[] header, string filePath, long offset)
        {
            _header = header;
            _file = File.OpenRead(filePath);
            _file.Position = offset;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_header);
            await _file.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _header.Length + (_file.Length - _file.Position);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void Absorb(JsonElement part, ref int inserted, List<JsonElement> failed)
    {
        if (part.TryGetProperty("inserted", out var n) && n.ValueKind == JsonValueKind.Number)
            inserted += n.GetInt32();
        if (part.TryGetProperty("failed", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var f in list.EnumerateArray()) failed.Add(f.Clone());
    }

    private static JsonElement CsvSummary(int inserted, List<JsonElement> failed, int? resumeRow, JsonElement? error)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("status", error is null ? "success" : "failed");
            w.WriteStartObject("attributes");
            w.WriteNumber("inserted", inserted);
            w.WriteNumber("failed_count", failed.Count);
            if (failed.Count > 0)
            {
                w.WriteStartArray("failed");
                foreach (var f in failed) f.WriteTo(w);
                w.WriteEndArray();
            }
            if (resumeRow is { } row) w.WriteNumber("resume_row", row);
            w.WriteEndObject();
            if (error is { } e)
            {
                w.WritePropertyName("error");
                e.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static JsonElement MessageJson(string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("message", message);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // displayname/description maps are en/ar/ku → text. Server's
    // RequestHandler.ParseTranslation accepts the {en,ar,ku} shape.
    public Task<JsonElement> AttachAsync(string shortname, string entryShortname, string payloadType, string filePath,
        Dictionary<string, string>? displayname = null, Dictionary<string, string>? description = null)
    {
        var sub = $"{CurrentSubpath}/{entryShortname}".Replace("//", "/");
        var attrs = new StringBuilder("\"is_active\":true");
        if (displayname is { Count: > 0 }) attrs.Append(",\"displayname\":").Append(BuildTranslationJson(displayname));
        if (description is { Count: > 0 }) attrs.Append(",\"description\":").Append(BuildTranslationJson(description));
        var recordJson = $"{{\"shortname\":\"{Esc(shortname)}\",\"resource_type\":\"{Esc(payloadType)}\",\"subpath\":\"{Esc(sub)}\",\"attributes\":{{{attrs}}}}}";
        return UploadWithPayloadAsync(recordJson, filePath);
    }

    private static string BuildTranslationJson(Dictionary<string, string> map)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in map)
        {
            if (!first) sb.Append(',');
            sb.Append($"\"{Esc(k)}\":\"{Esc(v)}\"");
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ---- Import / Export ----

    public async Task<JsonElement> ImportZipAsync(string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "zip_file", Path.GetFileName(filePath));
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/import", form));
        return await ParseAsync(resp);
    }

    // queryJson is the literal JSON body posted to /managed/export. Callers
    // either read it from a file (`export <query.json>`) or synthesize it
    // from CLI shortcut flags (`export --space S …`).
    public async Task<string> ExportAsync(string queryJson, string? outPath = null)
        => await DownloadAsync("/managed/export", queryJson, outPath, defaultName: $"{CurrentSpace}.zip");

    // CSV download — mirrors catalog's ModalCSVDownload; same Query body
    // shape as /managed/export, different MIME on the response.
    public async Task<string> ExportCsvAsync(string queryJson, string? outPath = null)
        => await DownloadAsync("/managed/csv", queryJson, outPath, defaultName: $"{CurrentSpace}.csv");

    // Raw byte fetch — used by --include-self to merge two zip responses
    // into one before writing to disk. Returns null on a non-2xx (caller
    // surfaces the error).
    public async Task<byte[]?> ExportToBytesAsync(string queryJson, string endpoint = "/managed/export")
    {
        var resp = await SendWithRefreshAsync(() => _http.PostAsync(endpoint, Json(queryJson)));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // Write pre-computed bytes (typically a merged zip) to disk using the
    // same default-path semantics as DownloadAsync.
    public async Task<string> WriteExportBytesAsync(byte[] bytes, string? outPath, string defaultName)
    {
        outPath = ResolveOutPath(outPath, defaultName);
        await File.WriteAllBytesAsync(outPath, bytes);
        return $"Exported to {outPath}";
    }

    private async Task<string> DownloadAsync(string endpoint, string queryJson, string? outPath, string defaultName)
    {
        var resp = await SendWithRefreshAsync(() => _http.PostAsync(endpoint, Json(queryJson)));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await ParseAsync(resp);
            return err.ToString();
        }
        outPath = ResolveOutPath(outPath, defaultName);
        await using var outFile = File.Create(outPath);
        await resp.Content.CopyToAsync(outFile);
        return $"Exported to {outPath}";
    }

    private static string ResolveOutPath(string? outPath, string defaultName)
    {
        if (outPath is null)
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            return Path.Combine(downloads, defaultName);
        }
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return outPath;
    }

    // ---- Query ----

    public Task<JsonElement> QueryAsync(string queryJson)
        => PostQueryAsync(queryJson);

    // ---- Meta / Payload ----

    public async Task<JsonElement> MetaAsync(string resourceType, string shortname)
    {
        var resp = await SendWithRefreshAsync(() =>
            _http.GetAsync($"/managed/meta/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}"));
        return await ParseAsync(resp);
    }

    public async Task<JsonElement> PayloadAsync(string resourceType, string shortname)
    {
        var resp = await SendWithRefreshAsync(() =>
            _http.GetAsync($"/managed/payload/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}.json"));
        return await ParseAsync(resp);
    }

    // ---- Request (raw JSON file) ----

    public async Task<JsonElement> RequestFromFileAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/request", Json(json)));
        return await ParseAsync(resp);
    }

    // ---- Internals ----

    private async Task<JsonElement> ManagedRequestAsync(string requestType, string recordJson)
    {
        var json = $"{{\"space_name\":\"{Esc(CurrentSpace)}\",\"request_type\":\"{Esc(requestType)}\",\"records\":[{recordJson}]}}";
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/request", Json(json)));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> PostQueryAsync(string queryJson)
    {
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/query", Json(queryJson)));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> UploadWithPayloadAsync(string recordJson, string filePath)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(recordJson, Encoding.UTF8, "application/json"), "request_record", "record.json");
        form.Add(new StringContent(CurrentSpace), "space_name");
        await using var fs = File.OpenRead(filePath);
        using var streamContent = new StreamContent(fs);
        form.Add(streamContent, "payload_file", Path.GetFileName(filePath));
        var resp = await SendWithRefreshAsync(() => _http.PostAsync("/managed/resource_with_payload", form));
        return await ParseAsync(resp);
    }

    // Build a single record JSON object for /managed/request
    private static string RecordJson(string resourceType, string subpath, string shortname, string? attrsInner)
    {
        var attrs = attrsInner is not null ? $"{{{attrsInner}}}" : "{}";
        return $"{{\"resource_type\":\"{Esc(resourceType)}\",\"subpath\":\"{Esc(subpath)}\",\"shortname\":\"{Esc(shortname)}\",\"attributes\":{attrs}}}";
    }

    private static StringContent Json(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement; }
        catch { return JsonDocument.Parse($"{{\"raw\":\"{Esc(text)}\"}}").RootElement; }
    }

    // Escape a string for embedding in JSON
    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    public void Dispose()
    {
        _http.Dispose();
        _uploadHttp.Dispose();
        if (_ownsHandler) _handler.Dispose();
    }
}
