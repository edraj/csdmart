using System.Text.Json;
using Dmart.Models.Api;
#if NET8_0_OR_GREATER
using Dmart.Client.Json;
#endif

namespace Dmart.Client;

// Remaining pydmart-parity methods. Split from DmartClient.cs so the core
// (auth + query + request + entry + upload) stays readable; wire-plumbing
// for the long tail of user / public / info endpoints lives here.
public sealed partial class DmartClient
{
    // ============================================================
    // User management
    // ============================================================

    // POST /user/create — register a new user. Server auto-logs them in and
    // returns the access_token inside records[0].attributes. Client does NOT
    // store the returned token (caller decides whether to adopt it).
    public async Task<Response> CreateUserAsync(Record record, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/user/create", Json(record));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /user/profile — update current user profile (email/msisdn/language/
    // displayname/description/payload/password).
    public async Task<Response> UpdateUserAsync(Record record, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/user/profile", Json(record));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // GET /user/check-existing?{property}={value} — "is this shortname/email/
    // msisdn already taken?" Useful for signup-form live validation.
    public async Task<Response> CheckExistingAsync(string property, string value, CancellationToken ct = default)
    {
        var escapedValue = Uri.EscapeDataString(value);
        using var req = BuildRequest(HttpMethod.Get, $"/user/check-existing?{property}={escapedValue}");
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /user/otp-request. `purpose` is one of "login", "reset",
    // "register" or "verify-contact" and selects what the code will be
    // redeemable for. Provide exactly one of msisdn, email or shortname.
    // The server always answers 200 Ok for a well-formed request, whether or
    // not a code was actually sent. "verify-contact" requires an
    // authenticated client; "register" works anonymously while
    // self-registration is enabled.
    public async Task<Response> OtpRequestAsync(
        string purpose, string? msisdn = null, string? email = null, string? shortname = null,
        string? acceptLanguage = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["purpose"] = purpose };
        if (!string.IsNullOrEmpty(msisdn))    body["msisdn"]    = msisdn;
        if (!string.IsNullOrEmpty(email))     body["email"]     = email;
        if (!string.IsNullOrEmpty(shortname)) body["shortname"] = shortname;
        using var req = BuildRequest(HttpMethod.Post, "/user/otp-request", Json(body));
        if (!string.IsNullOrEmpty(acceptLanguage))
            req.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // Convenience wrappers over OtpRequestAsync.
    public Task<Response> OtpRequestLoginAsync(
        string? msisdn = null, string? email = null, string? shortname = null,
        string? acceptLanguage = null, CancellationToken ct = default)
        => OtpRequestAsync("login", msisdn, email, shortname, acceptLanguage, ct);

    public Task<Response> PasswordResetRequestAsync(
        string? shortname = null, string? msisdn = null, string? email = null, CancellationToken ct = default)
        => OtpRequestAsync("reset", msisdn, email, shortname, null, ct);

    public Task<Response> OtpRequestRegisterAsync(
        string? msisdn = null, string? email = null,
        string? acceptLanguage = null, CancellationToken ct = default)
        => OtpRequestAsync("register", msisdn, email, null, acceptLanguage, ct);

    public Task<Response> OtpRequestVerifyContactAsync(
        string? msisdn = null, string? email = null,
        string? acceptLanguage = null, CancellationToken ct = default)
        => OtpRequestAsync("verify-contact", msisdn, email, null, acceptLanguage, ct);

    // Contact confirm/change: POST /user/profile — send `email` +
    // `email_otp` (or `msisdn` + `msisdn_otp`) to confirm the stored
    // contact, or `new_email`/`new_msisdn` + the OTP to change to a new one.

    // POST /user/verify-contact — prove control of an email or msisdn and make
    // it yours, verified. Same call whether the address is the one already on
    // your row or a new one: the server decides which from state it already
    // holds, so there is no `new_email` counterpart to get wrong.
    //
    // Requires a token, and the code must have been issued at the
    // verify-contact purpose TO THIS ADDRESS.
    //
    // The body key is "code". The pre-1.4.0 ConfirmOtpAsync this replaces sent
    // "otp", which the request record never bound to — so that method could
    // only ever fail.
    public async Task<Response> VerifyContactAsync(
        string code, string? msisdn = null, string? email = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["code"] = code };
        if (!string.IsNullOrEmpty(msisdn)) body["msisdn"] = msisdn;
        if (!string.IsNullOrEmpty(email))  body["email"]  = email;
        using var req = BuildRequest(HttpMethod.Post, "/user/verify-contact", Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /user/validate_password — server-side password-policy check.
    public async Task<Response> ValidatePasswordAsync(string password, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["password"] = password };
        using var req = BuildRequest(HttpMethod.Post, "/user/validate_password", Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // ============================================================
    // Data (managed / public)
    // ============================================================

    // POST /managed/csv — server-side CSV export of a Query. Response is
    // still a Response envelope with the CSV contents in attributes or an
    // attached media; see server's CsvService.
    public async Task<Response> CsvAsync(Query query, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "/managed/csv", Json(query));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // Convenience: list spaces via a single Query. Matches pydmart's
    // get_spaces(); always returns up to 100 to keep wire shape small.
    public Task<Response> GetSpacesAsync(CancellationToken ct = default)
        => QueryAsync(new Query
        {
            Type = Dmart.Models.Enums.QueryType.Spaces,
            SpaceName = "management",
            Subpath = "/",
            Limit = 100,
        }, scope: "managed", ct);

    // Convenience: list direct children of a subpath (Python's get_children).
    public Task<Response> GetChildrenAsync(
        string spaceName, string subpath, int limit = 20, int offset = 0,
        List<Dmart.Models.Enums.ResourceType>? restrictTypes = null, CancellationToken ct = default)
        => QueryAsync(new Query
        {
            Type = Dmart.Models.Enums.QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath,
            FilterTypes = restrictTypes,
            ExactSubpath = true,
            Search = "",
            Limit = limit,
            Offset = offset,
        }, scope: "managed", ct);

    // GET /managed/health/{space_name} — server health checks for a space.
    // Returns the raw JsonDocument (not wrapped in a Response envelope).
    public async Task<JsonDocument> GetSpaceHealthAsync(string spaceName, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"/managed/health/{Uri.EscapeDataString(spaceName)}");
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // GET /{scope}/payload/... — fetch raw payload bytes/JSON for a
    // resource. Returns the raw stream-parsed document so callers can
    // read binary attachments OR structured JSON as needed.
    public async Task<JsonDocument> GetPayloadAsync(
        string resourceType, string spaceName, string subpath, string shortname,
        string schemaShortname = "", string ext = ".json", string scope = "managed",
        CancellationToken ct = default)
    {
        subpath = NormalizeSubpath(subpath);
        var path = $"/{scope}/payload/{resourceType}/{spaceName}{subpath}/{shortname}{schemaShortname}{ext}";
        using var req = BuildRequest(HttpMethod.Get, path);
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // Builds a direct download URL for an attachment payload. Pure string
    // construction — no HTTP call — matching pydmart's behavior so callers
    // can pass the URL to a <img src>, browser download, etc.
    public string GetAttachmentUrl(
        string resourceType, string spaceName, string subpath, string parentShortname,
        string shortname, string? ext = null, string scope = "managed")
    {
        subpath = NormalizeSubpath(subpath);
        return $"{BaseUrl}/{scope}/payload/{resourceType}/{spaceName}{subpath}/{parentShortname}/{shortname}{ext ?? ""}";
    }

    // PUT /managed/progress-ticket/{space}/{subpath}/{shortname}/{action} —
    // advance a ticket through its workflow. Optional resolution/comment
    // appear in the request body matching Python's progress_ticket.
    public async Task<Response> ProgressTicketAsync(
        string spaceName, string subpath, string shortname, string action,
        string? resolution = null, string? comment = null, CancellationToken ct = default)
    {
        subpath = NormalizeSubpath(subpath);
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(resolution)) body["resolution"] = resolution;
        if (!string.IsNullOrEmpty(comment))    body["comment"]    = comment;
        using var req = BuildRequest(HttpMethod.Put,
            $"/managed/progress-ticket/{spaceName}{subpath}/{shortname}/{action}",
            Json(body));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // POST /managed/data-asset — execute a query against a data asset
    // (CSV / Parquet / JSONL / SQLite). queryString defaults to
    // "SELECT * FROM file" to retrieve everything.
    public async Task<JsonDocument> FetchDataAssetAsync(
        string resourceType, string dataAssetType, string spaceName, string subpath, string shortname,
        string queryString = "SELECT * FROM file",
        List<string>? filterDataAssets = null, string? branchName = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["space_name"]        = spaceName,
            ["resource_type"]     = resourceType,
            ["data_asset_type"]   = dataAssetType,
            ["subpath"]           = subpath,
            ["shortname"]         = shortname,
            ["query_string"]      = queryString,
            ["filter_data_assets"] = filterDataAssets,
            ["branch_name"]       = branchName,
        };
        using var req = BuildRequest(HttpMethod.Post, "/managed/data-asset", Json(body));
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // POST /public/submit/{space}/{resource_type?}/{workflow_shortname?}/{schema}/{subpath}
    // Anonymous or cross-realm submit of a record matching the schema.
    public async Task<Response> SubmitAsync(
        string spaceName, string schemaShortname, string subpath, Record record,
        Dmart.Models.Enums.ResourceType? resourceType = null,
        string? workflowShortname = null, CancellationToken ct = default)
    {
        var url = $"/public/submit/{spaceName}";
        if (resourceType is { } rt)
#if NET8_0_OR_GREATER
            url += $"/{JsonSerializer.Serialize(rt, DmartClientJsonContext.Default.ResourceType).Trim('"')}";
#else
            url += $"/{JsonSerializer.Serialize(rt, DefaultJsonOptions).Trim('"')}";
#endif
        if (!string.IsNullOrEmpty(workflowShortname)) url += $"/{workflowShortname}";
        url += $"/{schemaShortname}{NormalizeSubpath(subpath)}";
        using var req = BuildRequest(HttpMethod.Post, url, Json(record));
        return await SendEnvelopeAsync(req, ct).ConfigureAwait(false);
    }

    // ============================================================
    // Server introspection
    // ============================================================

    // GET /info/manifest — server version, plugins, runtime info.
    public async Task<JsonDocument> GetManifestAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/info/manifest");
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // GET /info/settings — effective DmartSettings (secrets redacted).
    public async Task<JsonDocument> GetSettingsAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/info/settings");
        return await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    // ============================================================
    // Raw-document helper (for endpoints that don't use the Response envelope)
    // ============================================================

    private async Task<JsonDocument> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new DmartException(0, new Error("ClientError", 500, ex.Message, null));
        }

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        // Same failure-envelope check as RetrieveEntryAsync — some endpoints
        // opportunistically return the Response shape on error.
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("status", out var statusProp)
            && statusProp.ValueKind == JsonValueKind.String
            && statusProp.GetString() == "failed"
            && doc.RootElement.TryGetProperty("error", out var errProp))
        {
#if NET8_0_OR_GREATER
            var err = JsonSerializer.Deserialize(errProp.GetRawText(), DmartClientJsonContext.Default.Error)
                ?? new Error(ErrorTypes.Request, 500, "unknown error", null);
#else
            var err = JsonSerializer.Deserialize<Error>(errProp.GetRawText(), DefaultJsonOptions)
                ?? new Error(ErrorTypes.Request, 500, "unknown error", null);
#endif
            doc.Dispose();
            throw MapException((int)resp.StatusCode, err);
        }
        return doc;
    }
}
