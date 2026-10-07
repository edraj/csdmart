using Dmart.Api.Mcp;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Mcp;

// An MCP session's push channel lives as long as the session, not as long as
// one SSE connection. GET /mcp used to complete the outbox writer on every
// exit, so a dropped stream (proxy idle timeout) permanently killed pushes for
// that session id; a reconnect read a completed channel and ended at once.
public class McpSessionOutboxLifetimeTests
{
    [Fact]
    public void Outbox_Accepts_Frames_Until_The_Session_Is_Removed()
    {
        var store = new McpSessionStore();
        var session = store.Create("test-client", "1.0", "2025-03-26");

        session.TryEnqueue("{\"a\":1}").ShouldBeTrue();
        // A reader coming and going must not close the channel — only Remove does.
        session.TryEnqueue("{\"a\":2}").ShouldBeTrue();

        store.Remove(session.Id).ShouldBeTrue();
        session.TryEnqueue("{\"a\":3}").ShouldBeFalse("Remove completes the outbox");
        store.Get(session.Id).ShouldBeNull();
    }
}
