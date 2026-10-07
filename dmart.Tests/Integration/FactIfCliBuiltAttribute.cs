using System.IO;
using System.Linq;
using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that drives the built `dmart` CLI binary. When no binary exists
// (a clean checkout that has not run `dotnet build`), xUnit SKIPS the test with
// a visible reason. The previous pattern — `Assert.True(true, "not built");
// return;` — reported a silent PASS, so three CLI end-to-end tests were green
// on any machine without the binary, and otherwise ran whatever stale binary
// happened to be there.
public sealed class FactIfCliBuiltAttribute : FactAttribute
{
    public FactIfCliBuiltAttribute()
    {
        if (Locate() is null)
            Skip = "dmart CLI binary not built (bin/{Release,Debug}/net10.0/dmart) — run `dotnet build` first";
    }

    internal static string? Locate()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "dmart.csproj")))
            d = d.Parent;
        if (d is null) return null;
        var candidates = new[]
        {
            Path.Combine(d.FullName, "bin", "Release", "net10.0", "dmart"),
            Path.Combine(d.FullName, "bin", "Debug",   "net10.0", "dmart"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
