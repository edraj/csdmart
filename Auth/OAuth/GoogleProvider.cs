namespace Dmart.Auth.OAuth;

public sealed class GoogleProvider
{
    public Task<string?> ExchangeAsync(string code, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
