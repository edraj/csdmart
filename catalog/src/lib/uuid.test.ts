import { afterEach, describe, expect, it, vi } from "vitest";
import { generateUUID } from "@shared/uuid";

const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe("generateUUID", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("uses crypto.randomUUID when the platform has it", () => {
    const randomUUID = vi.fn(() => "11111111-2222-4333-8444-555555555555");
    vi.stubGlobal("crypto", { randomUUID });
    expect(generateUUID()).toBe("11111111-2222-4333-8444-555555555555");
    expect(randomUUID).toHaveBeenCalledOnce();
  });

  // Plain-http deployments have `crypto` without `randomUUID` (it is gated to
  // secure contexts). "auto" shortnames must still get a UUID there.
  it("falls back to a well-formed v4 UUID when randomUUID is missing", () => {
    vi.stubGlobal("crypto", {});
    const a = generateUUID();
    const b = generateUUID();
    expect(a).toMatch(V4);
    expect(b).toMatch(V4);
    expect(a).not.toBe(b);
  });

  it("falls back when crypto is absent altogether", () => {
    vi.stubGlobal("crypto", undefined);
    expect(generateUUID()).toMatch(V4);
  });
});
