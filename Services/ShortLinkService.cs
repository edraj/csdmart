using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;

namespace Dmart.Services;

public sealed class ShortLinkService(LinkRepository links)
{
    public Task<string?> ResolveAsync(string token, CancellationToken ct = default)
        => links.ResolveAsync(token, ct);

    public Task<string> CreateAsync(string targetUrl, CancellationToken ct = default)
        => links.CreateAsync(targetUrl, ct);
}
