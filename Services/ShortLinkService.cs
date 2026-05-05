using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class ShortLinkService(LinkRepository links, IOptions<DmartSettings> settings)
{
    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        var url = await links.ResolveAsync(token, ct);
        if (url is null) return null;

        // The resolver runs anonymously (ShortLinkHandler.cs:21 .AllowAnonymous).
        // Today the creator only ever stores `{AppUrl}/managed/entry/content/...`,
        // so off-host stored URLs would be the result of a bug or a future writer
        // that bypasses the creator. Reject them at the redirect site so the
        // public endpoint can never become an open redirect.
        var appUrl = settings.Value.AppUrl;
        if (string.IsNullOrEmpty(appUrl)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var stored)) return null;
        if (!Uri.TryCreate(appUrl, UriKind.Absolute, out var app)) return url;
        if (!string.Equals(stored.Scheme, app.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(stored.Host, app.Host, StringComparison.OrdinalIgnoreCase)
            || stored.Port != app.Port)
            return null;
        return url;
    }

    public Task<string> CreateAsync(string targetUrl, CancellationToken ct = default)
        => links.CreateAsync(targetUrl, ct);

    public async Task CreateAsync(string token, string targetUrl, TimeSpan expires, CancellationToken ct = default)
    {
        await links.CreateWithTokenAsync(token, targetUrl, TimeUtils.Now().Add(expires), ct);
    }
}
