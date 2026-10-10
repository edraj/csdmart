namespace Dmart.Utils;

// The Content-Security-Policy of a sign-in page that posts to itself and, on
// success, redirects to a client's redirect URI (the OIDC provider's and the
// MCP authorization server's). Chromium applies `form-action` to every
// redirect that follows a form submission, so `form-action 'self'` alone
// blocks the final 302 to the client: the user signs in and is left on a
// blank page. The client's origin is added; nothing else is.
//
// Delivered in a <meta> element, where browsers ignore frame-ancestors (and
// Chromium logs an error saying so): framing is refused by the X-Frame-Options
// header ResponseHeadersMiddleware sends on every response.
public static class FormActionCsp
{
    public static string For(string? redirectUri)
        => "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'"
           + (Source(redirectUri) is { } source ? " " + source : "")
           + "; base-uri 'none'";

    // The CSP source expression for a redirect URI: its origin for http(s),
    // its scheme for a native app's private scheme (`cursor:`), or null for
    // anything that cannot be written as one safely.
    internal static string? Source(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var u)) return null;
        if (u.Scheme is "http" or "https")
            return u.IsDefaultPort ? $"{u.Scheme}://{u.IdnHost}" : $"{u.Scheme}://{u.IdnHost}:{u.Port}";
        // RFC 3986 scheme characters only: anything else could end the
        // directive or the policy.
        return u.Scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') ? u.Scheme + ":" : null;
    }
}
