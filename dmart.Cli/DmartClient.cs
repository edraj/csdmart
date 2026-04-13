using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dmart.Cli;

// HTTP client for dmart REST API — mirrors Python cli.py's DMart class.
public sealed class DmartClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CliSettings _settings;
    private string? _token;

    public List<string> SpaceNames { get; private set; } = new();
    public string CurrentSpace { get; set; }
    public string CurrentSubpath { get; set; } = "/";
    public List<JsonElement> CurrentEntries { get; private set; } = new();

    public DmartClient(CliSettings settings)
    {
        _settings = settings;
        CurrentSpace = settings.DefaultSpace;
        _http = new HttpClient { BaseAddress = new Uri(settings.Url.TrimEnd('/')) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---- Auth ----

    public async Task<(bool Ok, string? Error)> LoginAsync()
    {
        HttpResponseMessage resp;
        try
        {
            var body = JsonContent($"{{\"shortname\":\"{_settings.Shortname}\",\"password\":\"{_settings.Password}\"}}");
            resp = await _http.PostAsync("/user/login", body);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Cannot connect to {_settings.Url}: {ex.InnerException?.Message ?? ex.Message}");
        }
        var json = await ParseAsync(resp);
        if (json.TryGetProperty("status", out var st) && st.GetString() == "success")
        {
            _token = json.GetProperty("records")[0].GetProperty("attributes")
                .GetProperty("access_token").GetString();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return (true, null);
        }
        var msg = json.TryGetProperty("error", out var err) ? err.GetProperty("message").GetString() : "login failed";
        return (false, msg);
    }

    // ---- Spaces ----

    public async Task<List<string>> FetchSpacesAsync(bool force = false)
    {
        if (!force && SpaceNames.Count > 0) return SpaceNames;
        try
        {
            var resp = await PostQueryAsync(new { type = "spaces", space_name = "management", subpath = "/" });
            if (resp.TryGetProperty("records", out var recs))
                SpaceNames = recs.EnumerateArray().Select(r => r.GetProperty("shortname").GetString()!).ToList();
        }
        catch { /* query failed — use whatever we have */ }
        // Ensure the current/default space is always in the list
        if (!SpaceNames.Contains(CurrentSpace))
            SpaceNames.Insert(0, CurrentSpace);
        return SpaceNames;
    }

    // ---- Navigation ----

    public async Task ListAsync()
    {
        var resp = await PostQueryAsync(new
        {
            space_name = CurrentSpace,
            type = "subpath",
            subpath = CurrentSubpath.Replace("//", "/"),
            retrieve_json_payload = true,
            limit = 100,
        });
        CurrentEntries.Clear();
        if (resp.TryGetProperty("records", out var recs))
            CurrentEntries = recs.EnumerateArray().ToList();
    }

    // ---- CRUD ----

    public async Task<JsonElement> CreateFolderAsync(string shortname)
    {
        return await ManagedRequestAsync("create", new
        {
            resource_type = "folder",
            subpath = CurrentSubpath,
            shortname,
            attributes = new { is_active = true },
        });
    }

    public async Task<JsonElement> CreateEntryAsync(string shortname, string resourceType)
    {
        return await ManagedRequestAsync("create", new
        {
            resource_type = resourceType,
            subpath = CurrentSubpath,
            shortname,
            attributes = new { is_active = true },
        });
    }

    public async Task<JsonElement> DeleteAsync(string shortname, string resourceType)
    {
        return await ManagedRequestAsync("delete", new
        {
            resource_type = resourceType,
            subpath = CurrentSubpath,
            shortname,
            attributes = new { },
        });
    }

    public async Task<JsonElement> MoveAsync(string resourceType, string srcSubpath, string srcShortname,
        string destSubpath, string destShortname)
    {
        return await ManagedRequestAsync("move", new
        {
            resource_type = resourceType,
            subpath = CurrentSubpath,
            shortname = srcShortname,
            attributes = new
            {
                src_subpath = srcSubpath,
                src_shortname = srcShortname,
                dest_subpath = destSubpath,
                dest_shortname = destShortname,
            },
        });
    }

    public async Task<JsonElement> ManageSpaceAsync(string spaceName, string requestType)
    {
        var json = Serialize(new
        {
            space_name = spaceName,
            request_type = requestType,
            records = new[] { new { resource_type = "space", subpath = "/", shortname = spaceName, attributes = new { } } },
        });
        var resp = await _http.PostAsync("/managed/request", JsonContent(json));
        var result = await ParseAsync(resp);
        await FetchSpacesAsync(force: true);
        return result;
    }

    public async Task<JsonElement> ProgressTicketAsync(string subpath, string shortname, string action)
    {
        var resp = await _http.PutAsync($"/managed/progress-ticket/{CurrentSpace}/{subpath}/{shortname}/{action}", null);
        return await ParseAsync(resp);
    }

    // ---- Upload ----

    public async Task<JsonElement> UploadSchemaAsync(string shortname, string filePath)
    {
        var record = new
        {
            resource_type = "schema",
            subpath = "schema",
            shortname,
            attributes = new { schema_shortname = "meta_schema", is_active = true },
        };
        return await UploadWithPayloadAsync(record, filePath);
    }

    public async Task<JsonElement> UploadCsvAsync(string resourceType, string subpath, string schemaShortname, string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "resources_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync(
            $"/managed/resources_from_csv/{resourceType}/{CurrentSpace}/{subpath}/{schemaShortname}", form);
        return await ParseAsync(resp);
    }

    public async Task<JsonElement> AttachAsync(string shortname, string entryShortname, string payloadType, string filePath)
    {
        var record = new
        {
            shortname,
            resource_type = payloadType,
            subpath = $"{CurrentSubpath}/{entryShortname}".Replace("//", "/"),
            attributes = new { is_active = true },
        };
        return await UploadWithPayloadAsync(record, filePath);
    }

    // ---- Import / Export ----

    public async Task<JsonElement> ImportZipAsync(string filePath)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "zip_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync("/managed/import", form);
        return await ParseAsync(resp);
    }

    public async Task<string> ExportAsync(string queryJsonPath)
    {
        var queryJson = await File.ReadAllTextAsync(queryJsonPath);
        var resp = await _http.PostAsync("/managed/export", JsonContent(queryJson));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await ParseAsync(resp);
            return err.ToString();
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var outPath = Path.Combine(downloads, "export.zip");
        await using var outFile = File.Create(outPath);
        await resp.Content.CopyToAsync(outFile);
        return $"Exported to {outPath}";
    }

    // ---- Query ----

    public async Task<JsonElement> QueryAsync(object queryObj)
    {
        return await PostQueryAsync(queryObj);
    }

    // ---- Meta / Payload ----

    public async Task<JsonElement> MetaAsync(string resourceType, string shortname)
    {
        var resp = await _http.GetAsync($"/managed/meta/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}");
        return await ParseAsync(resp);
    }

    public async Task<JsonElement> PayloadAsync(string resourceType, string shortname)
    {
        var resp = await _http.GetAsync($"/managed/payload/{resourceType}/{CurrentSpace}/{CurrentSubpath}/{shortname}.json");
        return await ParseAsync(resp);
    }

    // ---- Request (raw JSON file) ----

    public async Task<JsonElement> RequestFromFileAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var resp = await _http.PostAsync("/managed/request", JsonContent(json));
        return await ParseAsync(resp);
    }

    // ---- Internals ----

    private async Task<JsonElement> ManagedRequestAsync(string requestType, object record)
    {
        var json = Serialize(new
        {
            space_name = CurrentSpace,
            request_type = requestType,
            records = new[] { record },
        });
        var resp = await _http.PostAsync("/managed/request", JsonContent(json));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> PostQueryAsync(object queryObj)
    {
        var resp = await _http.PostAsync("/managed/query", JsonContent(Serialize(queryObj)));
        return await ParseAsync(resp);
    }

    private async Task<JsonElement> UploadWithPayloadAsync(object record, string filePath)
    {
        using var form = new MultipartFormDataContent();
        var recordJson = Serialize(record);
        form.Add(new StringContent(recordJson, Encoding.UTF8, "application/json"), "request_record", "record.json");
        form.Add(new StringContent(CurrentSpace), "space_name");
        await using var fs = File.OpenRead(filePath);
        form.Add(new StreamContent(fs), "payload_file", Path.GetFileName(filePath));
        var resp = await _http.PostAsync("/managed/resource_with_payload", form);
        return await ParseAsync(resp);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        try { return JsonDocument.Parse(text).RootElement; }
        catch { return JsonDocument.Parse($"{{\"raw\":\"{text}\"}}").RootElement; }
    }

    private static string Serialize(object obj)
        => JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    public void Dispose() => _http.Dispose();
}
