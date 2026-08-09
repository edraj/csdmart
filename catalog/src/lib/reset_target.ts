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
import type { ResetIdentifier } from "@/lib/dmart_services/password_reset";

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
