import { describe, expect, it } from "vitest";
import { apiErrorKey, classifyApiError } from "./apiError";

describe("classifyApiError", () => {
  it("recognises axios transport failures before any status", () => {
    expect(classifyApiError({ code: "ERR_NETWORK" })).toBe("network");
    expect(classifyApiError({ code: "ECONNABORTED" })).toBe("timeout");
    expect(classifyApiError({ code: "ETIMEDOUT" })).toBe("timeout");
  });

  it("treats a request with no response as a network failure", () => {
    expect(classifyApiError({ request: {}, response: undefined })).toBe(
      "network",
    );
    expect(classifyApiError({ request: {}, response: null })).toBe("network");
  });

  it("maps HTTP statuses to categories", () => {
    expect(classifyApiError({ response: { status: 401 } })).toBe("unauthorized");
    expect(classifyApiError({ response: { status: 403 } })).toBe("forbidden");
    expect(classifyApiError({ response: { status: 404 } })).toBe("not_found");
    expect(classifyApiError({ response: { status: 500 } })).toBe("server");
    expect(classifyApiError({ response: { status: 503 } })).toBe("server");
  });

  it("accepts a top-level status as well as response.status", () => {
    expect(classifyApiError({ status: 404 })).toBe("not_found");
    expect(classifyApiError({ status: "403" })).toBe("forbidden");
  });

  it("falls back to unknown for anything else", () => {
    expect(classifyApiError(new Error("boom"))).toBe("unknown");
    expect(classifyApiError(null)).toBe("unknown");
    expect(classifyApiError(undefined)).toBe("unknown");
    expect(classifyApiError("string")).toBe("unknown");
    expect(classifyApiError({ response: { status: 418 } })).toBe("unknown");
  });
});

describe("apiErrorKey", () => {
  it("prefixes the category with the errors namespace", () => {
    expect(apiErrorKey({ response: { status: 404 } })).toBe("errors.not_found");
    expect(apiErrorKey(undefined)).toBe("errors.unknown");
  });
});
