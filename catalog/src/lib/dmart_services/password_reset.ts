/**
 * Password reset — catalog's binding of the shared flow.
 *
 * The transport, validation and sessionStorage handoff live in
 * ui-shared/password-reset.ts and are re-exported verbatim; the only thing
 * catalog owns is the translation from a neutral ResetFailureReason to
 * catalog's PascalCase i18n keys (cxb's copy does the same with snake_case
 * keys). That split is what stops the two frontends from drifting apart:
 * there is no longer a second copy of the password regex, the identifier
 * detection or the InternalErrorCode map to keep in sync by hand.
 */

import { Dmart } from "@edraj/tsdmart";
import {
  createPasswordResetClient,
  type ResetFailureReason,
} from "@shared/password-reset";

export * from "@shared/password-reset";

/**
 * catalog's own SDK copy — the workspaces are pinned to different tsdmart
 * versions, so this must be imported here rather than from the shared module,
 * or the flow would talk to a Dmart class that never received the axios
 * instance _module.svelte installs.
 *
 * Neither leg goes through a typed SDK method: password-reset-confirm was
 * never in the SDK, and passwordResetRequest now points at a deleted route.
 * Both go through the shared axios instance directly — the
 * same pattern cxb/src/routes/management/tools/import.svelte uses.
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
  | "ResetCodeInvalid"
  | "PasswordRequirements"
  | "ResetAccountLocked"
  | "ResetFailed";

// A total Record, not a switch with a default: a reason added upstream fails
// to compile here rather than silently falling through to "something went
// wrong".
const MESSAGE_KEYS: Record<ResetFailureReason, ResetErrorKey> = {
  code_invalid: "ResetCodeInvalid",
  password_rules: "PasswordRequirements",
  account_locked: "ResetAccountLocked",
  unknown: "ResetFailed",
};

export function resetErrorKey(reason: ResetFailureReason): ResetErrorKey {
  return MESSAGE_KEYS[reason];
}
