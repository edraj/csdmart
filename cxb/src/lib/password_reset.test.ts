import { describe, expect, it, vi } from "vitest";
import type { ResetFailureReason } from "@shared/password-reset";

// Importing the module constructs its transport, which reaches for cxb's
// tsdmart copy. Stubbed because the SDK is not what this file exercises — the
// shared client's behaviour is covered from catalog's suite, and what cxb owns
// is the reason -> message-key mapping below.
vi.mock("@edraj/tsdmart", () => ({
  Dmart: { passwordResetRequest: vi.fn(), axiosDmartInstance: { post: vi.fn() } },
}));

import { resetErrorKey } from "./password_reset";

/**
 * The whole point of the shared module returning a neutral ResetFailureReason
 * is that each app maps it to its own i18n scheme. cxb's keys are snake_case
 * where catalog's are PascalCase, and mixing them up produces a raw key
 * rendered in the UI rather than a message — which svelte-i18n will not flag.
 */
describe("resetErrorKey", () => {
  const cases: Array<[ResetFailureReason, string]> = [
    ["code_invalid", "reset_code_invalid"],
    ["password_rules", "password_requirements"],
    ["account_locked", "reset_account_locked"],
    ["unknown", "reset_failed"],
  ];

  it.each(cases)("maps %s to %s", (reason, key) => {
    expect(resetErrorKey(reason)).toBe(key);
  });

  it("returns cxb's snake_case keys, never catalog's PascalCase", () => {
    for (const [reason] of cases) {
      const key = resetErrorKey(reason);
      expect(key).toMatch(/^[a-z0-9_]+$/);
    }
  });

  // The map is typed Record<ResetFailureReason, ...>, so a reason added
  // upstream is a compile error rather than an undefined lookup. This asserts
  // the runtime side of that: every reason resolves to something renderable.
  it("resolves every reason to a non-empty key", () => {
    for (const [reason] of cases) {
      expect(resetErrorKey(reason)).toBeTruthy();
    }
  });
});
