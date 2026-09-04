using Dmart.Plugins.Native;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// A plugin sends its log level as a bare int over the wire, so the host has to
// treat every value as untrusted input. Anything outside LogLevel's range must
// land on a sane level rather than cast straight to the enum — a plugin that
// sent 99 would otherwise produce a LogLevel no filter matches, and the line
// would vanish.
//
// The rest of the logging pipeline (category prefixing, truncation, the JSONL
// sink) is covered end-to-end by Integration/PluginLogToJsonlTests.
public class PluginLoggingTests
{
    [Theory]
    [InlineData(0, LogLevel.Trace)]
    [InlineData(1, LogLevel.Debug)]
    [InlineData(2, LogLevel.Information)]
    [InlineData(3, LogLevel.Warning)]
    [InlineData(4, LogLevel.Error)]
    [InlineData(5, LogLevel.Critical)]
    [InlineData(6, LogLevel.None)]
    public void ClampLevel_Maps_Valid_Values(int raw, LogLevel expected)
        => NativePluginCallbacks.ClampLevel(raw).ShouldBe(expected);

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    public void ClampLevel_Coerces_OutOfRange_To_Information(int raw)
        => NativePluginCallbacks.ClampLevel(raw).ShouldBe(LogLevel.Information);
}
