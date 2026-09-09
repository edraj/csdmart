import { describe, expect, it } from "vitest";
import {
  canClearLockout,
  readFailedAttempts,
  resolveAttemptCount,
} from "@shared/user-lockout";

/**
 * The account lockout is `attempt_count` alone — a locked account is still
 * `is_active: true`. Both failure modes this guards are SILENT: the save
 * succeeds either way and the UI shows nothing, so neither would surface in a
 * smoke test or a manual click-through.
 */
describe("resolveAttemptCount", () => {
  it("omits the counter from an ordinary save", () => {
    expect(resolveAttemptCount(undefined, false)).toBeUndefined();
    expect(resolveAttemptCount(0, false)).toBeUndefined();
  });

  it("never echoes back the counter it read", () => {
    // The regression that matters. The form round-trips whatever the API
    // returned, so echoing a counter read seconds ago rolls back the increments
    // an in-flight brute-force run landed in between.
    expect(resolveAttemptCount(5, false)).toBeUndefined();
    expect(resolveAttemptCount(99, false)).toBeUndefined();
  });

  it("sends an explicit 0 when the admin asks to clear it", () => {
    expect(resolveAttemptCount(5, true)).toBe(0);
    // Also when there was nothing to clear — the request is still coherent.
    expect(resolveAttemptCount(undefined, true)).toBe(0);
  });
});

describe("the submitted payload", () => {
  // The form assigns `undefined` and relies on JSON.stringify dropping the key,
  // exactly as it already does for `password`. Assert against the real
  // serialization rather than the in-memory object, because that is what
  // reaches /managed/request.
  const onTheWire = (attrs: Record<string, unknown>) =>
    JSON.parse(JSON.stringify(attrs)) as Record<string, unknown>;

  it("drops attempt_count entirely on an ordinary save of a locked user", () => {
    const loadedFromApi = { shortname: "someone", is_active: true, attempt_count: 5 };
    const submitted = onTheWire({
      ...loadedFromApi,
      displayname: { en: "Renamed" },
      attempt_count: resolveAttemptCount(loadedFromApi.attempt_count, false),
    });

    expect("attempt_count" in submitted).toBe(false);
    // is_active still rides along on every save — which is precisely why it
    // cannot be the unlock gesture.
    expect(submitted.is_active).toBe(true);
  });

  it("carries attempt_count: 0 when the admin ticks clear-on-save", () => {
    const loadedFromApi = { shortname: "someone", is_active: true, attempt_count: 5 };
    const submitted = onTheWire({
      ...loadedFromApi,
      attempt_count: resolveAttemptCount(loadedFromApi.attempt_count, true),
    });

    expect(submitted.attempt_count).toBe(0);
  });
});

describe("readFailedAttempts", () => {
  it("reads a real counter", () => {
    expect(readFailedAttempts(1)).toBe(1);
    expect(readFailedAttempts(5)).toBe(5);
  });

  it("treats a user who has never failed a login as 0", () => {
    // The column is nullable and a fresh row carries no counter at all.
    expect(readFailedAttempts(undefined)).toBe(0);
    expect(readFailedAttempts(null)).toBe(0);
    expect(readFailedAttempts(0)).toBe(0);
  });

  it("does not render junk as a count", () => {
    expect(readFailedAttempts("")).toBe(0);
    expect(readFailedAttempts("not a number")).toBe(0);
    expect(readFailedAttempts({})).toBe(0);
    expect(readFailedAttempts(-3)).toBe(0);
    expect(readFailedAttempts(Number.NaN)).toBe(0);
    // A hand-edited meta payload can deliver the number as a string.
    expect(readFailedAttempts("4")).toBe(4);
  });
});

describe("canClearLockout", () => {
  it("offers the control only when there is something to clear", () => {
    expect(canClearLockout(3)).toBe(true);
    expect(canClearLockout(0)).toBe(false);
    expect(canClearLockout(undefined)).toBe(false);
  });
});
