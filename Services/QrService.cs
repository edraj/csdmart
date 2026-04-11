using Dmart.Models.Core;

namespace Dmart.Services;

public sealed class QrService
{
    public Task<byte[]> GenerateAsync(Locator l, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task<bool> ValidateAsync(string payload, CancellationToken ct = default)
        => Task.FromResult(true);
}
