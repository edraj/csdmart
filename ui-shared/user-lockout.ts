// The account lockout is `users.attempt_count`, and nothing else.
//
// A locked account is still `is_active: true` — the lock never touches that
// flag, which means "an admin deactivated this account". So the counter is the
// only thing that says an account is locked out, and it is also the only way to
// clear one.
//
// That makes the admin form's handling of this single field load-bearing in two
// opposite directions, which is why it lives here rather than inline in each
// app's MetaUserForm:
//
//   * an ORDINARY save must never carry attempt_count. The form round-trips
//     whatever the API returned, so echoing the counter back would write a value
//     read seconds earlier — rolling back the increments an in-flight
//     brute-force run landed in between, and handing the attacker those attempts
//     back. Same reason the form never submits `password`.
//
//   * an explicit UNLOCK must carry exactly `0`. Not `is_active: true`: the form
//     emits that flag on every save, so keying an unlock off it would mean
//     renaming a locked user silently cancels their lockout. The server keys on
//     the value, so 0 is unambiguous and lands in the audit history.
//
// Both failure modes are silent — the request succeeds either way and the UI
// shows nothing — so neither would surface in a smoke test.

// What to put in the submitted attributes. `undefined` rather than "omit the
// key" because that is how both forms already suppress a field: the value is
// assigned and JSON.stringify drops it on the way out, exactly as with
// `password`. `loaded` is accepted and deliberately ignored — the signature
// documents that the previously-read counter is never echoed back, and the test
// pins it.
export function resolveAttemptCount(
  loaded: unknown,
  resetRequested: boolean,
): number | undefined {
  return resetRequested ? 0 : undefined;
}

// The counter to display next to the "clear on save" control. Absent, null and
// junk all read as 0: a user row that has never failed a login carries no
// counter at all, and "0" is the honest thing to show for it.
export function readFailedAttempts(loaded: unknown): number {
  if (typeof loaded === "number") {
    return Number.isFinite(loaded) && loaded > 0 ? Math.floor(loaded) : 0;
  }
  // A JSON number can arrive as a string through a hand-edited meta payload.
  if (typeof loaded === "string" && loaded.trim() !== "") {
    const n = Number(loaded);
    return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
  }
  return 0;
}

// Whether to offer the unlock control at all. Kept as a named predicate rather
// than `> 0` at three call sites so the meaning survives: the UI cannot say
// "locked" on its own, because the threshold is server-side config
// (MAX_FAILED_LOGIN_ATTEMPTS) that the form does not know. It can only say the
// counter is non-zero and offer to clear it.
export function canClearLockout(loaded: unknown): boolean {
  return readFailedAttempts(loaded) > 0;
}
