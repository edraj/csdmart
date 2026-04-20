namespace Dmart.Utils;

// Thin helpers for the repeated `http.User.Identity?.Name` dance that
// appears at the top of nearly every handler. `Actor()` returns the
// shortname of the authenticated caller or null for anonymous; the
// "OrAnonymous" variant resolves to the string literal "anonymous" so
// downstream code that wants a non-null actor (plugin events, request
// logs, owner fields) has one place to change if the default ever moves.
public static class HttpContextExtensions
{
    public static string? Actor(this HttpContext ctx) => ctx.User.Identity?.Name;

    public static string ActorOrAnonymous(this HttpContext ctx)
        => ctx.User.Identity?.Name ?? "anonymous";
}
