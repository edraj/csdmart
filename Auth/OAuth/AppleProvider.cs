namespace Dmart.Auth.OAuth;

public sealed class AppleProvider
{
    public Task<string?> ExchangeAsync(string code, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
