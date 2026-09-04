/**
 * Password reset — cxb's binding of the shared flow.
 *
 * The transport, validation and sessionStorage handoff live in
 * ui-shared/password-reset.ts and are re-exported verbatim. cxb owns only the
 * translation from a neutral ResetFailureReason to cxb's snake_case i18n keys
 * — catalog's copy of this file does the same with PascalCase keys, and that
 * naming difference is the one thing the two apps genuinely disagree about.
 *
 * cxb has no test runner; the shared module is unit-tested from catalog's
 * suite, which now covers this code path for both apps.
 */

import { Dmart } from "@edraj/tsdmart";
import {
  createPasswordResetClient,
  type ResetFailureReason,
} from "@shared/password-reset";

export * from "@shared/password-reset";

/**
 * cxb's own SDK copy — the workspaces are pinned to different tsdmart
 * versions, so this must be imported here rather than from the shared module,
 * or the flow would talk to a Dmart class that never received the axios
 * instance ensureDmartAxios() installs.
 *
 * Neither leg goes through a typed SDK method: password-reset-confirm was
 * never in the SDK, and passwordResetRequest now points at a deleted route.
 * Both go through the shared axios instance directly — the
 * same pattern src/routes/management/tools/import.svelte uses.
 */
const client = createPasswordResetClient({
  // Direct, not Dmart.passwordResetRequest: that SDK method targets
  // /user/password-reset-request, which no longer exists — leg 1 is
  // /user/otp-request with purpose=reset now. Same reason leg 2 goes direct.
  requestReset: (body) =>
    Dmart.axiosDmartInstance.post("user/otp-request", body),
  confirmReset: async (body) =>
    (await Dmart.axiosDmartInstance.post("user/password-reset-confirm", body))
      ?.data,
});

export const requestPasswordReset = client.requestPasswordReset;
export const confirmPasswordReset = client.confirmPasswordReset;

export type ResetErrorKey =
  | "reset_code_invalid"
  | "password_requirements"
  | "reset_account_locked"
  | "reset_failed";

// A total Record, not a switch with a default: a reason added upstream fails
// to compile here rather than silently falling through to "something went
// wrong".
const MESSAGE_KEYS: Record<ResetFailureReason, ResetErrorKey> = {
  code_invalid: "reset_code_invalid",
  password_rules: "password_requirements",
  account_locked: "reset_account_locked",
  unknown: "reset_failed",
};

export function resetErrorKey(reason: ResetFailureReason): ResetErrorKey {
  return MESSAGE_KEYS[reason];
}
