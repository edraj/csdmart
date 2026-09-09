// Where the API lives, from the runtime `backend` field in config.json.
//
// An empty `backend` means "same origin as the page" — the deployment where
// dmart itself serves the SPA, which is every containerised install and the
// default for `dmart init`. That convention was already written down in
// catalog's websocket.ts and cxb's ListView, but the two axios instances did
// not honour it, so it could not actually be used: a blank or absent `backend`
// left `baseURL` undefined, and the SDK's paths are RELATIVE WITHOUT A LEADING
// SLASH —
//
//     axiosDmartInstance.post('user/login')
//
// — so they resolve against the current document URL, not the origin. With cxb
// served under `<base href="/cxb/">`, a login from /cxb/management/content/foo
// went to /cxb/management/content/user/login. A 404 that looks like a broken
// backend rather than a broken base URL.
//
// Which is why config.json could not simply drop the field either: something
// has to name the origin, and only the browser knows it. Hence this.
//
// Returns a base with no trailing slash, so callers can concatenate `/ws` or
// `/ws-info` onto it and `new URL()` can parse it. Empty only when there is no
// configured value AND no origin (server-side rendering, tests) — callers that
// need a parseable URL check for that, as the WebSocket paths do.
export function resolveBackendBase(configured: unknown, origin?: string): string {
  const explicit = typeof configured === "string" ? configured.trim() : "";
  const base = explicit || (origin ?? "").trim();
  return base.replace(/\/+$/, "");
}

// The value to hand axios as `baseURL`.
//
// Separate from resolveBackendBase because axios treats an empty string as "no
// base at all" and falls back to document-relative resolution — the exact bug
// above. "/" is the honest answer when the origin is unknown: it at least
// anchors requests at the root of whatever host served the page, rather than
// under the SPA's own path prefix.
export function resolveAxiosBaseUrl(configured: unknown, origin?: string): string {
  return resolveBackendBase(configured, origin) || "/";
}
