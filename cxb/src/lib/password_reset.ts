/**
 * Password reset — client half of POST /user/password-reset-request and
 * POST /user/password-reset-confirm (Api/User/OtpHandler.cs).
 *
 * Deliberately duplicated from catalog/src/lib/dmart_services/password_reset.ts
 * and catalog/src/lib/reset_target.ts (including the sessionStorage helpers
 * setResetTarget / getResetTarget / clearResetTarget / setResetDone /
 * consumeResetDone / setResetStartOver / consumeResetStartOver): the two
 * frontends share no code and use different component libraries. The logic here is unit-tested on the catalog
 * side (cxb has no test runner) — keep the two in sync when either changes.
 *
 * Only email and msisdn are supported. The backend also accepts a shortname
 * identifier; this flow deliberately does not expose it.
 *
 * INTENTIONAL DIVERGENCE from catalog's copy: cxb's i18n keys are lowercase
 * snake_case, while catalog's are PascalCase. `ResetErrorKey` and `mapCode`
 * below return cxb's key names (e.g. "reset_code_invalid" instead of
 * "ResetCodeInvalid"). Do not "fix" this back to match catalog when syncing.
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
  | "reset_code_invalid"
  | "password_requirements"
  | "reset_account_locked"
  | "reset_failed";

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
      return "reset_code_invalid"; // OTP_INVALID — mismatch, expired, or no such user
    case 17:
      return "password_requirements"; // INVALID_PASSWORD_RULES
    case 110:
      return "reset_account_locked"; // USER_ACCOUNT_LOCKED
    default:
      return "reset_failed";
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

/**
 * Step-1 -> step-2 handoff for the password reset flow.
 *
 * sessionStorage rather than the localStorage-backed `storage` helper: a
 * pending reset target must not outlive the tab. It is not a URL parameter
 * either — that would put the user's email or phone number into browser
 * history, server access logs and referrer headers.
 *
 * Every access is wrapped: private-browsing modes and quota failures throw on
 * plain sessionStorage access, and losing a handoff must degrade to "start
 * over", never to an uncaught error.
 */
const KEY = "pwdResetTarget";

export function setResetTarget(id: ResetIdentifier): void {
  try {
    sessionStorage.setItem(KEY, JSON.stringify(id));
  } catch {
    // Private mode or quota exceeded — step 2 will bounce back to step 1.
  }
}

export function getResetTarget(): ResetIdentifier | null {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (parsed?.kind !== "email" && parsed?.kind !== "msisdn") return null;
    if (typeof parsed.value !== "string" || !parsed.value) return null;
    return parsed as ResetIdentifier;
  } catch {
    return null;
  }
}

export function clearResetTarget(): void {
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    // ignore
  }
}

/**
 * One-shot UI flags handed across a navigation (step 2 -> login for the
 * success notice, step 2 -> step 1 for "start over"). Guarded exactly like the
 * target helpers above: a private-browsing write that throws must never break
 * the navigation it accompanies, and must never be mistaken for a failed
 * reset.
 */
const DONE_KEY = "pwdResetDone";
const START_OVER_KEY = "pwdResetStartOver";

export function setResetDone(): void {
  try {
    sessionStorage.setItem(DONE_KEY, "1");
  } catch {
    // Private mode or quota exceeded — the user just misses the notice.
  }
}

export function consumeResetDone(): boolean {
  try {
    const was = sessionStorage.getItem(DONE_KEY) === "1";
    sessionStorage.removeItem(DONE_KEY);
    return was;
  } catch {
    return false;
  }
}

export function setResetStartOver(): void {
  try {
    sessionStorage.setItem(START_OVER_KEY, "1");
  } catch {
    // Private mode or quota exceeded — step 1 just omits the notice.
  }
}

export function consumeResetStartOver(): boolean {
  try {
    const was = sessionStorage.getItem(START_OVER_KEY) === "1";
    sessionStorage.removeItem(START_OVER_KEY);
    return was;
  } catch {
    return false;
  }
}
