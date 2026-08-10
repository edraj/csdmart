// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  RESEND_COOLDOWN_SECONDS,
  clearResetTarget,
  consumeResetDone,
  consumeResetStartOver,
  getResetTarget,
  markResetCodeIssued,
  resendSecondsRemaining,
  setResetDone,
  setResetStartOver,
  setResetTarget,
} from "@shared/password-reset";

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

describe("resend cooldown", () => {
  beforeEach(() => {
    sessionStorage.clear();
    vi.restoreAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("reports the full cooldown immediately after a code is issued", () => {
    markResetCodeIssued();
    expect(resendSecondsRemaining()).toBe(RESEND_COOLDOWN_SECONDS);
  });

  // The bug this guards: the countdown used to restart at 60 on every mount,
  // so refreshing step 2 at t=50s cost the user another full minute for a
  // resend the server would already have accepted.
  it("counts down as real time passes, surviving a remount", () => {
    markResetCodeIssued();
    vi.advanceTimersByTime(50_000);
    expect(resendSecondsRemaining()).toBe(10);
  });

  it("reaches zero once the cooldown has elapsed", () => {
    markResetCodeIssued();
    vi.advanceTimersByTime(RESEND_COOLDOWN_SECONDS * 1000);
    expect(resendSecondsRemaining()).toBe(0);
  });

  it("never goes negative", () => {
    markResetCodeIssued();
    vi.advanceTimersByTime(10 * 60 * 1000);
    expect(resendSecondsRemaining()).toBe(0);
  });

  it("assumes a full cooldown when nothing was recorded", () => {
    expect(resendSecondsRemaining()).toBe(RESEND_COOLDOWN_SECONDS);
  });

  it("assumes a full cooldown for a malformed timestamp", () => {
    sessionStorage.setItem("pwdResetIssuedAt", "not-a-number");
    expect(resendSecondsRemaining()).toBe(RESEND_COOLDOWN_SECONDS);
  });

  it("assumes a full cooldown when the clock moves backwards", () => {
    sessionStorage.setItem("pwdResetIssuedAt", String(Date.now() + 30_000));
    expect(resendSecondsRemaining()).toBe(RESEND_COOLDOWN_SECONDS);
  });

  it("is cleared along with the target", () => {
    markResetCodeIssued();
    vi.advanceTimersByTime(50_000);
    clearResetTarget();
    expect(resendSecondsRemaining()).toBe(RESEND_COOLDOWN_SECONDS);
  });

  it("swallows a throwing sessionStorage write", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("QuotaExceededError");
    });
    expect(() => markResetCodeIssued()).not.toThrow();
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
