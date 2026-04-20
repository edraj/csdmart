// Read <base href> from index.html so absolute app paths like "/login" work
// both at root and under a sub-path deployment (e.g. <base href="/abc/">).
export function withBasePrefix(path: string): string {
  const baseHref = document.querySelector("base")?.getAttribute("href") || "/";
  const prefix = baseHref.replace(/^\/|\/$/g, "");
  return prefix ? `/${prefix}${path}` : path;
}
