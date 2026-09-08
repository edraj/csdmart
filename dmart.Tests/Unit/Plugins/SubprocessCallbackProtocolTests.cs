using System.Text.Json;
using Dmart.Plugins.Native;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Drives a real subprocess through SubprocessPluginHost to pin the callback
// read loop. A fake in unit-test space could not catch what actually breaks
// here — the loop's job is to tell a callback frame apart from the exchange's
// final response on a live pipe, and to keep an older plugin that never sends
// one behaving exactly as it did before callbacks existed.
//
// The fake plugins are POSIX shell rather than Python so the suite depends on
// nothing beyond /bin/sh. Each `echo` is its own write(2), which is the
// line-at-a-time flushing the protocol requires.
[Collection(Dmart.Tests.Integration.PluginInvocationContextCollection.Name)]
public class SubprocessCallbackProtocolTests : IDisposable
{
    private readonly List<string> _dirs = new();
    private readonly IServiceProvider? _priorServices;

    public SubprocessCallbackProtocolTests()
    {
        // Callbacks land in the shared Emit* implementations, which read this
        // static. Forced to null so these cases exercise the transport without
        // a database; restored so a later integration class sees its provider.
        _priorServices = NativePluginCallbacks.Services;
        NativePluginCallbacks.SetServicesForTesting(null);
    }

    public void Dispose()
    {
        NativePluginCallbacks.SetServicesForTesting(_priorServices);
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    // Writes an executable fake plugin into a fresh directory named after the
    // plugin, matching the layout NativePluginLoader.FindExecutable expects.
    private string WritePlugin(string shortname, string script)
    {
        var root = Path.Combine(Path.GetTempPath(), "dmart-cbtest-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, shortname);
        Directory.CreateDirectory(dir);
        _dirs.Add(root);

        var exe = Path.Combine(dir, shortname);
        File.WriteAllText(exe, script);
        // CA1416: these fakes are /bin/sh scripts, so the whole class is
        // POSIX-only by construction — there is no Windows path to guard.
#pragma warning disable CA1416
        File.SetUnixFileMode(exe,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        File.WriteAllText(Path.Combine(dir, "config.json"),
            $$"""{"shortname":"{{shortname}}","is_active":true,"type":"hook","listen_time":"after"}""");
        return root;
    }

    private SubprocessPluginHost Host(string shortname, string script)
        => new(Path.Combine(WritePlugin(shortname, script), shortname, shortname), shortname);

    [Fact]
    public void Plugin_That_Never_Calls_Back_Behaves_Exactly_As_Before()
    {
        // The compatibility guarantee. Every plugin already deployed is this
        // plugin: one line in, one line out, no knowledge of callbacks.
        using var host = Host("plain", """
            #!/bin/sh
            while IFS= read -r line; do
              echo '{"status":"ok","seen":"plain"}'
            done
            """);

        var response = host.SendAndReceive("""{"type":"hook","event":{}}""");
        JsonDocument.Parse(response).RootElement.GetProperty("seen").GetString().ShouldBe("plain");
    }

    [Fact]
    public void Callback_Is_Serviced_Before_The_Final_Response()
    {
        // The plugin splices dmart's callback_result into its own response, so
        // asserting on the response proves the host answered on stdin mid-
        // exchange rather than treating the frame as the final word.
        using var host = Host("onecb", """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"hook"'*)
                  echo '{"type":"callback","id":42,"op":"log","args":{"level":2,"message":"from plugin"}}'
                  IFS= read -r cb
                  printf '{"status":"ok","got":%s}\n' "$cb"
                  ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);

        var response = host.SendAndReceive("""{"type":"hook","event":{}}""");
        var got = JsonDocument.Parse(response).RootElement.GetProperty("got");

        got.GetProperty("type").GetString().ShouldBe("callback_result");
        got.GetProperty("id").GetInt32().ShouldBe(42);
        got.GetProperty("ok").GetBoolean().ShouldBeTrue();
        got.GetProperty("result").GetProperty("code").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Many_Callbacks_Can_Share_One_Exchange()
    {
        // One exchange is no longer one round trip. Three in a row, each
        // answered before the next is sent, then the real response.
        using var host = Host("multicb", """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"hook"'*)
                  n=0
                  while [ $n -lt 3 ]; do
                    echo '{"type":"callback","id":1,"op":"log","args":{"message":"tick"}}'
                    IFS= read -r cb
                    n=$((n+1))
                  done
                  printf '{"status":"ok","count":%s}\n' "$n"
                  ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);

        var response = host.SendAndReceive("""{"type":"hook","event":{}}""");
        JsonDocument.Parse(response).RootElement.GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void A_Response_Mentioning_Callback_Is_Not_Mistaken_For_A_Frame()
    {
        // The read loop pre-filters on the substring "callback" to keep
        // JsonDocument.Parse off the hot path. That filter is allowed false
        // positives but no false negatives — so a response that happens to
        // contain the word must still come back as the response.
        using var host = Host("wordy", """
            #!/bin/sh
            while IFS= read -r line; do
              echo '{"status":"ok","message":"finished the callback work"}'
            done
            """);

        var response = host.SendAndReceive("""{"type":"hook","event":{}}""");
        var root = JsonDocument.Parse(response).RootElement;
        root.GetProperty("status").GetString().ShouldBe("ok");
        root.GetProperty("message").GetString()!.ShouldContain("callback");
    }

    [Fact]
    public void Unparseable_Line_Is_Returned_As_The_Response()
    {
        // Even when it trips the "callback" pre-filter. The caller owns the
        // parse failure and can describe it with context the host lacks.
        using var host = Host("garbage", """
            #!/bin/sh
            while IFS= read -r line; do
              echo 'not json at all, callback'
            done
            """);

        host.SendAndReceive("""{"type":"hook","event":{}}""").ShouldContain("not json at all");
    }

    [Fact]
    public void A_Callback_Loop_Is_Capped_Rather_Than_Spinning_Forever()
    {
        // A per-line timeout cannot catch this: the plugin answers promptly
        // every time and simply never stops. Without the cap the exchange holds
        // the host lock indefinitely and the hook never returns.
        using var host = Host("flood", """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"hook"'*)
                  while :; do
                    echo '{"type":"callback","id":1,"op":"log","args":{"message":"x"}}'
                    IFS= read -r cb || exit 0
                  done
                  ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);

        var response = host.SendAndReceive("""{"type":"hook","event":{}}""");
        var root = JsonDocument.Parse(response).RootElement;
        root.GetProperty("status").GetString().ShouldBe("error");
        root.GetProperty("message").GetString()!.ShouldContain("callback limit");
    }

    [Fact]
    public void Info_Handshake_Advertises_Callback_Support()
    {
        // Negotiation is what stops an SDK-updated plugin from stranding itself
        // against an older dmart, so assert the real loader sends it — not just
        // that the constant exists. The fake records the line it was asked.
        var root = WritePlugin("handshake", """
            #!/bin/sh
            while IFS= read -r line; do
              case "$line" in
                *'"type":"info"'*)
                  printf '%s' "$line" > "$(dirname "$0")/info-seen.txt"
                  echo '{"shortname":"handshake","type":"hook","version":"1.0.0"}'
                  ;;
                *) echo '{"status":"ok"}' ;;
              esac
            done
            """);

        var services = new ServiceCollection();
        var failures = NativePluginLoader.ScanRoot(services, root);
        failures.ShouldBeEmpty();

        var seen = File.ReadAllText(Path.Combine(root, "handshake", "info-seen.txt"));
        var info = JsonDocument.Parse(seen).RootElement;
        info.GetProperty("type").GetString().ShouldBe("info");
        info.GetProperty("host").GetProperty("callbacks").GetInt32().ShouldBe(1);
    }
}
