using System.Security.Cryptography;

namespace Dmart.Auth;

public sealed class OtpProvider
{
    public string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public Task SendAsync(string destination, string code, CancellationToken ct = default)
        => Task.CompletedTask;   // TODO: hook SMS / email gateway

    public Task<bool> VerifyAsync(string destination, string code, CancellationToken ct = default)
        => Task.FromResult(true);
}
