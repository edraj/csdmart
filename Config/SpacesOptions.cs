namespace Dmart.Config;

public sealed class SpacesOptions
{
    public List<string> Allowed { get; set; } = new();
    public Dictionary<string, string>? Aliases { get; set; }
}
