/**
 * Password reset — client half of the two-leg flow in Api/User/OtpHandler.cs
 * (POST /user/password-reset-request, POST /user/password-reset-confirm).
 *
 * Only email and msisdn are supported. The backend also accepts a shortname
 * identifier; this flow deliberately does not expose it.
 */

import { Dmart } from "@edraj/tsdmart";

export type ResetIdentifier =
  | { kind: "email"; value: string }
  | { kind: "msisdn"; value: string };

// Same expression login.svelte uses, so an address accepted here is an
// address accepted at sign-in.
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const MSISDN_RE = /^\+?\d{6,}$/;

/**
 * Mirror of Auth/PasswordRules.Pattern: 8-64 characters drawn from the allowed
 * set, with at least one digit (ASCII or Arabic-Indic) and at least one
 * uppercase ASCII or Arabic letter. Kept in sync by hand — if the two ever
 * diverge, the server rejects with INVALID_PASSWORD_RULES (17), which
 * confirmPasswordReset maps back to the same message.
 */
const PASSWORD_RE =
  /^(?=.*[0-9٠-٩])(?=.*[A-Zء-ي])[a-zA-Zء-ي0-9٠-٩ _#@%*!?$^&()+={}\[\]~|;:,.<>\/-]{8,64}$/;

export function isValidResetPassword(password: string): boolean {
  return PASSWORD_RE.test(password);
}

/**
 * Classifies free-text input as an email or an msisdn, or null when it is
 * neither. Emails are lowercased because the server compares them
 * case-insensitively; msisdns are stripped of the separators people type.
 */
export function detectIdentifier(raw: string): ResetIdentifier | null {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  if (EMAIL_RE.test(trimmed)) return { kind: "email", value: trimmed.toLowerCase() };
  const digits = trimmed.replace(/[\s\-()]/g, "");
  if (MSISDN_RE.test(digits)) return { kind: "msisdn", value: digits };
  return null;
}

export type ResetErrorKey =
  | "ResetCodeInvalid"
  | "PasswordRequirements"
  | "ResetAccountLocked"
  | "ResetFailed";

/** Carries an i18n key rather than a message: the caller renders it via $_(). */
export class ResetError extends Error {
  readonly messageKey: ResetErrorKey;
  readonly code?: number;

  constructor(messageKey: ResetErrorKey, code?: number) {
    super(messageKey);
    this.name = "ResetError";
    this.messageKey = messageKey;
    this.code = code;
  }
}

/** Maps InternalErrorCode values to the i18n key the user should see. */
function mapCode(code: number | undefined): ResetErrorKey {
  switch (code) {
    case 307:
      return "ResetCodeInvalid"; // OTP_INVALID — mismatch, expired, or no such user
    case 17:
      return "PasswordRequirements"; // INVALID_PASSWORD_RULES
    case 110:
      return "ResetAccountLocked"; // USER_ACCOUNT_LOCKED
    default:
      return "ResetFailed";
  }
}

function identifierBody(id: ResetIdentifier): Record<string, string> {
  return id.kind === "email" ? { email: id.value } : { msisdn: id.value };
}

/**
 * Leg 1: ask the server to send a reset OTP.
 *
 * A 2xx says nothing about whether the account exists — the endpoint answers
 * identically for unknown users, mismatched emails and requests inside the 60s
 * resend cooldown, by design. Anything that throws here is transport failure
 * or the auth-by-ip rate limiter, not a user error.
 */
export async function requestPasswordReset(id: ResetIdentifier): Promise<void> {
  await Dmart.passwordResetRequest(identifierBody(id));
}

/**
 * Leg 2: verify the OTP and set the new password.
 *
 * Not in the tsdmart SDK (it has passwordResetRequest but no confirm), so this
 * goes through the shared axios instance — the same pattern
 * cxb/src/routes/management/tools/import.svelte uses.
 */
export async function confirmPasswordReset(
  id: ResetIdentifier,
  otp: string,
  password: string,
): Promise<void> {
  try {
    const { data } = await Dmart.axiosDmartInstance.post(
      "user/password-reset-confirm",
      { ...identifierBody(id), otp, password },
    );
    // Failures normally arrive as non-2xx via the catch below; this guards the
    // case where a 2xx nonetheless carries status: "failed".
    if (data?.status === "failed") {
      throw new ResetError(mapCode(data?.error?.code), data?.error?.code);
    }
  } catch (e: any) {
    if (e instanceof ResetError) throw e;
    const code = e?.response?.data?.error?.code;
    throw new ResetError(mapCode(code), code);
  }
}
