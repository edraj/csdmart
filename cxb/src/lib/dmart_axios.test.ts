// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The 401 interceptor in dmart_axios.ts signs the user out and reloads the
 * page. Its guard has four clauses, and the module's own comment calls two of
 * them load-bearing:
 *
 *   - only codes 47/48/49, because account lockout (110) also arrives as a
 *     401 and reacting to it would sign the user out spuriously;
 *   - only with a stored authToken, because an expected 401 on a
 *     password-reset page (no session at all) would otherwise reload the page
 *     out from under the form.
 *
 * Both failure modes are the kind that only show up in someone's face, never
 * in a stack trace, so they are pinned here.
 */

const setAxiosInstance = vi.fn();
const debouncedShowToast = vi.fn();

// A stub axios instance that captures the handlers passed to
// interceptors.response.use, so the rejection path can be driven directly
// without a network layer.
let captured: { onRejected?: (e: unknown) => unknown } = {};

vi.mock("axios", () => ({
  default: {
    create: vi.fn(() => ({
      interceptors: {
        response: {
          use: (_ok: unknown, onRejected: (e: unknown) => unknown) => {
            captured.onRejected = onRejected;
          },
        },
      },
    })),
  },
}));
vi.mock("@edraj/tsdmart", () => ({ Dmart: { setAxiosInstance } }));
vi.mock("@/config", () => ({
  website: { backend: "http://backend.test", backend_timeout: 5000 },
}));
vi.mock("@/utils/toast", () => ({ Level: { warn: "warn" } }));
vi.mock("@/utils/debounce", () => ({ debouncedShowToast }));

const reload = vi.fn();

/**
 * Fresh module state per test: both `instance` and `isRedirectingToLogin` are
 * module scope, and the latch in particular is one-way by design.
 */
async function freshInterceptor() {
  vi.resetModules();
  captured = {};
  const { ensureDmartAxios } = await import("./dmart_axios");
  ensureDmartAxios();
  return captured.onRejected!;
}

function unauthorized(code: number) {
  return { response: { status: 401, data: { error: { code } } } };
}

beforeEach(() => {
  localStorage.clear();
  reload.mockClear();
  setAxiosInstance.mockClear();
  debouncedShowToast.mockClear();
  // jsdom's location.reload is not implemented; replace the whole object so
  // the interceptor's call is observable instead of a console warning.
  Object.defineProperty(window, "location", {
    configurable: true,
    writable: true,
    value: { reload },
  });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("ensureDmartAxios", () => {
  it("installs the instance into the SDK", async () => {
    await freshInterceptor();
    expect(setAxiosInstance).toHaveBeenCalledTimes(1);
  });

  it("is idempotent — later callers reuse the first instance", async () => {
    vi.resetModules();
    const { ensureDmartAxios } = await import("./dmart_axios");
    const first = ensureDmartAxios();
    const second = ensureDmartAxios();
    expect(second).toBe(first);
    expect(setAxiosInstance).toHaveBeenCalledTimes(1);
  });
});

describe("401 handling", () => {
  it.each([47, 48, 49])("signs out and reloads on code %i", async (code) => {
    const onRejected = await freshInterceptor();
    localStorage.setItem("authToken", "tok");
    localStorage.setItem("user", "u");
    localStorage.setItem("permissions", "{}");
    localStorage.setItem("roles", "[]");

    await expect(onRejected(unauthorized(code))).rejects.toBeDefined();

    expect(localStorage.getItem("authToken")).toBeNull();
    expect(localStorage.getItem("user")).toBeNull();
    expect(localStorage.getItem("permissions")).toBeNull();
    expect(localStorage.getItem("roles")).toBeNull();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  // Account lockout is a 401 too. Signing the user out here would replace
  // "your account is locked" with a silent bounce to the login screen.
  it("ignores USER_ACCOUNT_LOCKED (110) despite the 401", async () => {
    const onRejected = await freshInterceptor();
    localStorage.setItem("authToken", "tok");

    await expect(onRejected(unauthorized(110))).rejects.toBeDefined();

    expect(localStorage.getItem("authToken")).toBe("tok");
    expect(reload).not.toHaveBeenCalled();
  });

  it("ignores other 401 error codes", async () => {
    const onRejected = await freshInterceptor();
    localStorage.setItem("authToken", "tok");

    await expect(onRejected(unauthorized(46))).rejects.toBeDefined();
    await expect(onRejected(unauthorized(50))).rejects.toBeDefined();

    expect(localStorage.getItem("authToken")).toBe("tok");
    expect(reload).not.toHaveBeenCalled();
  });

  it("ignores a non-401 status carrying one of those codes", async () => {
    const onRejected = await freshInterceptor();
    localStorage.setItem("authToken", "tok");

    await expect(
      onRejected({ response: { status: 403, data: { error: { code: 47 } } } }),
    ).rejects.toBeDefined();

    expect(reload).not.toHaveBeenCalled();
  });

  // The anonymous case: password-reset pages live outside /management and have
  // no session, so a 401 there is expected and must not reload the form away.
  it("does nothing without a stored authToken", async () => {
    const onRejected = await freshInterceptor();

    await expect(onRejected(unauthorized(47))).rejects.toBeDefined();

    expect(reload).not.toHaveBeenCalled();
  });

  it("reloads only once even if more 401s arrive", async () => {
    const onRejected = await freshInterceptor();
    localStorage.setItem("authToken", "tok");

    await expect(onRejected(unauthorized(47))).rejects.toBeDefined();
    localStorage.setItem("authToken", "tok-again");
    await expect(onRejected(unauthorized(47))).rejects.toBeDefined();

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("rejects with the original error, so callers still see it", async () => {
    const onRejected = await freshInterceptor();
    const err = unauthorized(47);
    localStorage.setItem("authToken", "tok");
    await expect(onRejected(err)).rejects.toBe(err);
  });
});

describe("network errors", () => {
  it("toasts on ERR_NETWORK", async () => {
    const onRejected = await freshInterceptor();
    await expect(onRejected({ code: "ERR_NETWORK" })).rejects.toBeDefined();
    expect(debouncedShowToast).toHaveBeenCalledTimes(1);
  });

  it("does not toast for an ordinary HTTP error", async () => {
    const onRejected = await freshInterceptor();
    await expect(onRejected({ response: { status: 500 } })).rejects.toBeDefined();
    expect(debouncedShowToast).not.toHaveBeenCalled();
  });

  it("survives an error with no response at all", async () => {
    const onRejected = await freshInterceptor();
    await expect(onRejected(new Error("boom"))).rejects.toThrow("boom");
    expect(reload).not.toHaveBeenCalled();
  });
});
