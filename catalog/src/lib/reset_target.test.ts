// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  clearResetTarget,
  consumeResetDone,
  consumeResetStartOver,
  getResetTarget,
  setResetDone,
  setResetStartOver,
  setResetTarget,
} from "./reset_target";

describe("reset target handoff", () => {
  beforeEach(() => {
    sessionStorage.clear();
    vi.restoreAllMocks();
  });

  it("round-trips an email identifier", () => {
    setResetTarget({ kind: "email", value: "a@b.co" });
    expect(getResetTarget()).toEqual({ kind: "email", value: "a@b.co" });
  });

  it("round-trips an msisdn identifier", () => {
    setResetTarget({ kind: "msisdn", value: "+9647701234567" });
    expect(getResetTarget()).toEqual({ kind: "msisdn", value: "+9647701234567" });
  });

  it("returns null when nothing was stored", () => {
    expect(getResetTarget()).toBeNull();
  });

  it("returns null after clearing", () => {
    setResetTarget({ kind: "email", value: "a@b.co" });
    clearResetTarget();
    expect(getResetTarget()).toBeNull();
  });

  it("returns null for malformed JSON", () => {
    sessionStorage.setItem("pwdResetTarget", "{not json");
    expect(getResetTarget()).toBeNull();
  });

  it("returns null for an unrecognised kind", () => {
    sessionStorage.setItem(
      "pwdResetTarget",
      JSON.stringify({ kind: "shortname", value: "alice" }),
    );
    expect(getResetTarget()).toBeNull();
  });

  it("returns null when the value is missing or not a string", () => {
    sessionStorage.setItem("pwdResetTarget", JSON.stringify({ kind: "email" }));
    expect(getResetTarget()).toBeNull();
    sessionStorage.setItem("pwdResetTarget", JSON.stringify({ kind: "email", value: 7 }));
    expect(getResetTarget()).toBeNull();
  });

  it("does not throw when sessionStorage writes fail", () => {
    vi.spyOn(sessionStorage, "setItem").mockImplementation(() => {
      throw new Error("QuotaExceededError");
    });
    expect(() => setResetTarget({ kind: "email", value: "a@b.co" })).not.toThrow();
  });

  it("does not throw when sessionStorage reads fail", () => {
    vi.spyOn(sessionStorage, "getItem").mockImplementation(() => {
      throw new Error("SecurityError");
    });
    expect(getResetTarget()).toBeNull();
  });
});

describe("one-shot reset flags", () => {
  beforeEach(() => {
    sessionStorage.clear();
    vi.restoreAllMocks();
  });

  it("consumeResetDone is false when the flag was never set", () => {
    expect(consumeResetDone()).toBe(false);
  });

  it("consumeResetDone reports the flag once, then clears it", () => {
    setResetDone();
    expect(consumeResetDone()).toBe(true);
    expect(consumeResetDone()).toBe(false);
  });

  it("consumeResetStartOver reports the flag once, then clears it", () => {
    setResetStartOver();
    expect(consumeResetStartOver()).toBe(true);
    expect(consumeResetStartOver()).toBe(false);
  });

  it("setResetDone swallows a throwing sessionStorage write", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("QuotaExceededError");
    });
    expect(() => setResetDone()).not.toThrow();
    expect(() => setResetStartOver()).not.toThrow();
  });

  it("consume helpers swallow a throwing sessionStorage read", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("SecurityError");
    });
    expect(consumeResetDone()).toBe(false);
    expect(consumeResetStartOver()).toBe(false);
  });
});
