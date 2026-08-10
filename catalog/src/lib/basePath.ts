// Read <base href> from index.html so absolute app paths like "/login" work
// both at root and under a sub-path deployment (e.g. <base href="/abc/">).
export function withBasePrefix(path: string): string {
  const baseHref = document.querySelector("base")?.getAttribute("href") || "/";
  const prefix = baseHref.replace(/^\/|\/$/g, "");
  return prefix ? `/${prefix}${path}` : path;
}

// Inverse of withBasePrefix: turns a live window.location.pathname back into
// the app-relative path that route literals (e.g. "/login",
// "/reset-password") are written in. Without this, comparing a real pathname
// like "/cat/reset-password" against "/reset-password" always fails and every
// public route is treated as protected.
export function stripBasePrefix(path: string): string {
  const baseHref = document.querySelector("base")?.getAttribute("href") || "/";
  const prefix = baseHref.replace(/^\/|\/$/g, "");
  if (!prefix) return path;
  // Segment boundary check: with prefix "cat", "/catalogs" must be left alone
  // (a plain startsWith would maul it into "alogs"). Only "/cat" itself and
  // "/cat/..." are prefixed paths.
  if (path !== `/${prefix}` && !path.startsWith(`/${prefix}/`)) return path;
  return path.slice(prefix.length + 1) || "/";
}
