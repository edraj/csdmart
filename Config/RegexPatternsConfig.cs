using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Dmart.Config;

[JsonSerializable(typeof(RegexPatternsConfig.RawFile))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
internal partial class RegexPatternsJsonContext : JsonSerializerContext;

// Format-only: whether a channel is enabled lives on DmartSettings
// (RegistrationEnabledChannels), not here — this class just validates shape.
public sealed class RegexPatternsConfig
{
    public const string DefaultEmailPattern =
        @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$";

    // Optional leading '+' and the 6-digit floor both match the existing
    // msisdn convention (Auth/OtpProvider.cs::IsMsisdn); 15 is the E.164
    // maximum. Deployments with shorter local numbering plans can relax
    // this via the regex.json override.
    public const string DefaultMsisdnPattern = @"^\+?[0-9]{6,15}$";

    private readonly Regex _emailRegex;
    private readonly Regex _msisdnRegex;

    public RegexPatternsConfig(IOptions<DmartSettings> options, ILogger<RegexPatternsConfig> log)
    {
        var raw = Load(options.Value.RegexConfigPath, log);
        _emailRegex = CompileOrDefault(raw?.Email, DefaultEmailPattern, "email", log);
        _msisdnRegex = CompileOrDefault(raw?.Msisdn, DefaultMsisdnPattern, "msisdn", log);
    }

    public string? ValidateEmailFormat(string? email)
        => Validate(_emailRegex, email, "Email format is invalid");

    public string? ValidateMsisdnFormat(string? msisdn)
        => Validate(_msisdnRegex, msisdn, "MSISDN format is invalid");

    private static string? Validate(Regex regex, string? value, string invalidMessage)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return regex.IsMatch(value) ? null : invalidMessage;
        }
        catch (RegexMatchTimeoutException)
        {
            // A ReDoS-prone override pattern hit the 100ms match timeout.
            // The value is unverifiable — report it as invalid (a 400 to the
            // caller) rather than letting the exception surface as a 500.
            return invalidMessage;
        }
    }

    private static Regex CompileOrDefault(string? pattern, string fallback, string channel, ILogger log)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? fallback : pattern;
        try
        {
            // Match timeout defends against a ReDoS-prone pattern in the config.
            return new Regex(effective, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            log.LogError(ex, "regex config: invalid regex for channel {Channel} — falling back to default", channel);
            return new Regex(fallback, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
    }

    private static RawFile? Load(string configuredPath, ILogger log)
    {
        var path = ResolvePath(configuredPath);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize(text, RegexPatternsJsonContext.Default.RawFile);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to load regex config at {Path}", path);
            return null;
        }
    }

    private static string? ResolvePath(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        return Path.Combine(home, ".dmart", "regex.json");
    }

    internal sealed class RawFile
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
    }
}
