import { describe, expect, it } from "vitest";
import { resolveAxiosBaseUrl, resolveBackendBase } from "@shared/backend-url";

/**
 * The bug these exist to prevent is invisible in the obvious place. A blank
 * `backend` left axios with no base, and the SDK's paths carry no leading
 * slash — `axiosDmartInstance.post('user/login')` — so they resolved against
 * the current document URL. Under `<base href="/cxb/">` that sent a login from
 * /cxb/management/content/foo to /cxb/management/content/user/login: a 404 that
 * reads as a broken backend rather than a broken base URL.
 */
describe("resolveAxiosBaseUrl", () => {
  it("falls back to the page origin when backend is blank", () => {
    // The same-origin deployment: dmart serves the SPA, so the API is wherever
    // the browser already is.
    expect(resolveAxiosBaseUrl("", "https://dmart.example")).toBe("https://dmart.example");
    expect(resolveAxiosBaseUrl("   ", "https://dmart.example")).toBe("https://dmart.example");
    expect(resolveAxiosBaseUrl(undefined, "https://dmart.example")).toBe("https://dmart.example");
  });

  it("never returns a blank base", () => {
    // Blank is the one value axios must not receive: it means "resolve against
    // the document URL", which is the bug. "/" at least anchors at the host root.
    expect(resolveAxiosBaseUrl("", "")).toBe("/");
    expect(resolveAxiosBaseUrl(undefined, undefined)).toBe("/");
  });

  it("honours an explicitly configured backend", () => {
    // A separately hosted API is still a supported deployment.
    expect(resolveAxiosBaseUrl("https://api.example", "https://ui.example"))
      .toBe("https://api.example");
  });

  it("keeps a path prefix but drops the trailing slash", () => {
    // Trailing slash matters: axios joins base and path with exactly one, so a
    // kept slash yields //user/login.
    expect(resolveAxiosBaseUrl("https://api.example/dmart/", "https://ui.example"))
      .toBe("https://api.example/dmart");
  });
});

describe("the URL axios ends up requesting", () => {
  // Mirrors axios's own join: base without trailing slash + "/" + path without
  // leading slash. Asserting the composed URL is the point — the base in
  // isolation looks fine in every broken case above.
  const requestUrl = (base: string, path: string) =>
    `${base.replace(/\/+$/, "")}/${path.replace(/^\/+/, "")}`;

  it("hits the API root, not the SPA's path prefix", () => {
    const base = resolveAxiosBaseUrl("", "https://dmart.example");
    expect(requestUrl(base, "user/login")).toBe("https://dmart.example/user/login");
  });

  it("does not vary with the route the user is on", () => {
    // The old failure was route-dependent: deeper routes produced deeper wrong
    // URLs. The origin is fixed, so the request URL has to be too.
    const base = resolveAxiosBaseUrl("", "https://dmart.example");
    expect(requestUrl(base, "managed/query")).toBe("https://dmart.example/managed/query");
  });
});

describe("resolveBackendBase", () => {
  it("strips trailing slashes so a suffix can be appended", () => {
    // Callers build `${base}/ws` and `${base}/ws-info`, and feed it to new URL().
    expect(resolveBackendBase("https://api.example/", "")).toBe("https://api.example");
    expect(resolveBackendBase("https://api.example///", "")).toBe("https://api.example");
  });

  it("returns empty when there is nothing to resolve", () => {
    // Distinct from resolveAxiosBaseUrl: the WebSocket paths check for empty and
    // decline to connect rather than build ws://undefined.
    expect(resolveBackendBase("", "")).toBe("");
    expect(resolveBackendBase(undefined, undefined)).toBe("");
  });

  it("ignores non-string configured values", () => {
    expect(resolveBackendBase(null, "https://dmart.example")).toBe("https://dmart.example");
    expect(resolveBackendBase(42, "https://dmart.example")).toBe("https://dmart.example");
  });

  it("produces a parseable base for the WebSocket URL", () => {
    // The shape catalog's websocket.ts and cxb's ListView both depend on.
    const base = resolveBackendBase("", "https://dmart.example");
    const parsed = new URL(base);
    expect(parsed.protocol).toBe("https:");
    expect(`${parsed.protocol === "https:" ? "wss:" : "ws:"}//${parsed.host}/ws`)
      .toBe("wss://dmart.example/ws");
  });
});
