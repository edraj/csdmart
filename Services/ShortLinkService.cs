using Dmart.DataAdapters.Sql;
using Dmart.Utils;

namespace Dmart.Services;

public sealed class ShortLinkService(LinkRepository links)
{
    public Task<string?> ResolveAsync(string token, CancellationToken ct = default)
        => links.ResolveAsync(token, ct);

    public Task<string> CreateAsync(string targetUrl, CancellationToken ct = default)
        => links.CreateAsync(targetUrl, ct);

    public async Task CreateAsync(string token, string targetUrl, TimeSpan expires, CancellationToken ct = default)
    {
        await links.CreateWithTokenAsync(token, targetUrl, TimeUtils.Now().Add(expires), ct);
    }
}
