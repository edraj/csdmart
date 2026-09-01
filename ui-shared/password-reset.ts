/**
 * Password reset — client half of the two-leg flow in Api/User/OtpHandler.cs
 * (POST /user/otp-request with purpose=reset, then
 * POST /user/password-reset-confirm).
 *
 * Shared by both frontends. catalog/ and cxb/ are yarn workspaces of the repo
 * root, so this file is reachable from both as "@shared/password-reset" (see
 * the resolve.alias entry in each vite.config.ts). Everything here is
 * UI-library agnostic — no Svelte, no i18n — because that is the only part the
 * two apps actually agree on. Failures surface as a neutral
 * ResetFailureReason, which each app maps to its own message keys (catalog
 * uses PascalCase, cxb snake_case).
 *
 * This module deliberately does NOT import @edraj/tsdmart. The two workspaces
 * are pinned to different SDK versions (catalog ^5.4.0, cxb ^5.3.4), so yarn
 * hoists one and nests the other; a bare import from here would resolve to the
 * hoisted copy for both apps. That copy is a *different class object* from the
 * one catalog's _module.svelte calls Dmart.setAxiosInstance() on, so
 * axiosDmartInstance would read back undefined and every request would die as
 * a swallowed TypeError — the exact failure cxb/src/lib/dmart_axios.ts was
 * written to fix. Each app injects its own SDK through ResetTransport instead.
 *
 * Only email and msisdn are supported. The backend also accepts a shortname
 * identifier; this flow deliberately does not expose it.
 */

export type ResetIdentifier =
  | { kind: "email"; value: string }
  | { kind: "msisdn"; value: string };

// Same expression the sign-in forms use, so an address accepted here is an
// address accepted at sign-in.
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

// Mirror of RegexPatternsConfig.DefaultMsisdnPattern: optional leading '+',
// then 6-15 digits (15 is the E.164 maximum). The upper bound matters.
//
// Leg 1 is /user/otp-request now, which DOES validate the format server-side
// — so unlike the old /password-reset-request, a malformed number comes back
// as an error rather than a silent Ok(). Keeping the check here anyway: the
// server's answer is deliberately uniform for everything else (unknown user,
// cooldown, daily cap), so a field-level rejection is still the only feedback
// a user gets that is specific to what they typed, and it happens before the
// request spends one of their daily OTP slots.
const MSISDN_RE = /^\+?\d{6,15}$/;

/**
 * Mirror of Auth/PasswordRules.Pattern: 8-64 characters drawn from the allowed
 * set, with at least one digit (ASCII or Arabic-Indic) and at least one
 * uppercase ASCII or Arabic letter. Kept in sync by hand — if the two ever
 * diverge, the server rejects with INVALID_PASSWORD_RULES (17), which
 * confirmPasswordReset maps back to the same message.
 *
 * Note that the second lookahead accepts *any* Arabic letter: Arabic is
 * unicameral, so there is no capital to demand. The user-facing requirements
 * string has to describe both halves of that rule, not just the Latin one.
 */
const PASSWORD_RE =
  /^(?=.*[0-9٠-٩])(?=.*[A-Zء-ي])[a-zA-Zء-ي0-9٠-٩ _#@%*!?$^&()+={}\[\]~|;:,.<>\/-]{8,64}$/;

/**
 * OTP lifetime in minutes, mirroring DmartSettings.OtpTokenTtl (default 300s).
 * Interpolated into the "code sent" message rather than written into each
 * translation, so an operator who retunes the setting has one constant to
 * follow instead of five translation files. Not served in config.json today —
 * if it ever is, read it from there instead.
 */
export const OTP_TTL_MINUTES = 5;

/**
 * Resend cooldown in seconds, mirroring
 * DmartSettings.AllowOtpResendAfter (default 60) — one cooldown per
 * destination across every OTP purpose now, not a reset-specific one. Inside this window
 * the server silently no-ops a resend, so blocking the button locally is the
 * only feedback the user can get. Same config caveat as OTP_TTL_MINUTES.
 */
export const RESEND_COOLDOWN_SECONDS = 60;

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

/**
 * Why a reset attempt failed, in terms neither frontend's i18n scheme owns.
 * Each app holds a Record<ResetFailureReason, ...> of its own message keys, so
 * adding a reason here is a compile error there rather than a silently missing
 * message.
 */
export type ResetFailureReason =
  | "code_invalid"
  | "password_rules"
  | "account_locked"
  | "unknown";

/** Carries a reason rather than a message: the caller renders it via $_(). */
export class ResetError extends Error {
  readonly reason: ResetFailureReason;
  readonly code?: number;

  constructor(reason: ResetFailureReason, code?: number) {
    super(reason);
    this.name = "ResetError";
    this.reason = reason;
    this.code = code;
  }
}

/** Maps InternalErrorCode values to the reason the user should be told. */
function mapCode(code: number | undefined): ResetFailureReason {
  switch (code) {
    case 307:
      return "code_invalid"; // OTP_INVALID — mismatch, expired, or no such user
    case 17:
      return "password_rules"; // INVALID_PASSWORD_RULES
    case 110:
      return "account_locked"; // USER_ACCOUNT_LOCKED
    default:
      return "unknown";
  }
}

function identifierBody(id: ResetIdentifier): Record<string, string> {
  return id.kind === "email" ? { email: id.value } : { msisdn: id.value };
}

/** The response body shape both legs answer with. */
export interface ResetResponseBody {
  status?: string;
  error?: { code?: number };
}

/**
 * The two calls this flow makes, supplied by whichever app is using it so the
 * shared code never resolves the SDK itself (see the note at the top of the
 * file). Both must reject on non-2xx the way axios does — the error mapping
 * below reads `e.response.data.error.code`.
 */
export interface ResetTransport {
  /**
   * POST /user/otp-request with `purpose: "reset"`.
   *
   * Not /user/password-reset-request — the per-purpose OTP routes were
   * collapsed into one endpoint that requires an explicit purpose, and the
   * old path no longer exists. The purpose is added here rather than in each
   * app's transport because it names the FLOW, not the caller: an app that
   * forgot it would get INVALID_DATA at leg 1 with nothing to say why.
   */
  requestReset(body: Record<string, string>): Promise<unknown>;
  /** POST /user/password-reset-confirm, resolving to the response body. */
  confirmReset(body: Record<string, unknown>): Promise<ResetResponseBody | undefined>;
}

export interface PasswordResetClient {
  /**
   * Leg 1: ask the server to send a reset OTP.
   *
   * A 2xx says nothing about whether the account exists — the endpoint answers
   * identically for unknown users, mismatched emails and requests inside the
   * resend cooldown, by design. Anything that throws here is transport failure
   * or the auth-by-ip rate limiter, not a user error.
   */
  requestPasswordReset(id: ResetIdentifier): Promise<void>;

  /** Leg 2: verify the OTP and set the new password. */
  confirmPasswordReset(
    id: ResetIdentifier,
    otp: string,
    password: string,
  ): Promise<void>;
}

export function createPasswordResetClient(
  transport: ResetTransport,
): PasswordResetClient {
  return {
    async requestPasswordReset(id) {
      await transport.requestReset({ ...identifierBody(id), purpose: "reset" });
    },

    async confirmPasswordReset(id, otp, password) {
      try {
        const data = await transport.confirmReset({
          ...identifierBody(id),
          otp,
          password,
        });
        // Failures normally arrive as non-2xx via the catch below; this guards
        // the case where a 2xx nonetheless carries status: "failed".
        if (data?.status === "failed") {
          throw new ResetError(mapCode(data?.error?.code), data?.error?.code);
        }
      } catch (e: any) {
        if (e instanceof ResetError) throw e;
        const code = e?.response?.data?.error?.code;
        throw new ResetError(mapCode(code), code);
      }
    },
  };
}

/**
 * Step-1 -> step-2 handoff.
 *
 * sessionStorage rather than a localStorage-backed helper: a pending reset
 * target must not outlive the tab. It is not a URL parameter either — that
 * would put the user's email or phone number into browser history, server
 * access logs and referrer headers.
 *
 * Every access is wrapped: private-browsing modes and quota failures throw on
 * plain sessionStorage access, and losing a handoff must degrade to "start
 * over", never to an uncaught error.
 */
const KEY = "pwdResetTarget";
const ISSUED_AT_KEY = "pwdResetIssuedAt";

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
    sessionStorage.removeItem(ISSUED_AT_KEY);
  } catch {
    // ignore
  }
}

/**
 * Records when the server was last asked to send a code, so the resend
 * cooldown survives a reload of step 2. Without it the countdown restarts at
 * the full RESEND_COOLDOWN_SECONDS on every mount, and a user who refreshes
 * after 50s waits another 60 for a resend the server would already accept.
 */
export function markResetCodeIssued(): void {
  try {
    sessionStorage.setItem(ISSUED_AT_KEY, String(Date.now()));
  } catch {
    // Private mode or quota exceeded — resendSecondsRemaining falls back to a
    // full cooldown, which is the conservative direction to fail in.
  }
}

/** Seconds left before a resend is worth attempting; 0 means "go ahead". */
export function resendSecondsRemaining(): number {
  try {
    const raw = sessionStorage.getItem(ISSUED_AT_KEY);
    if (!raw) return RESEND_COOLDOWN_SECONDS;
    const issuedAt = Number(raw);
    if (!Number.isFinite(issuedAt)) return RESEND_COOLDOWN_SECONDS;
    const elapsed = Math.floor((Date.now() - issuedAt) / 1000);
    // A negative elapsed means the clock moved backwards under us; treat it as
    // "just issued" rather than handing out a free resend.
    if (elapsed < 0) return RESEND_COOLDOWN_SECONDS;
    return Math.max(0, RESEND_COOLDOWN_SECONDS - elapsed);
  } catch {
    return RESEND_COOLDOWN_SECONDS;
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
