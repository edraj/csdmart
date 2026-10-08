// Turns whatever a failed API call rejected with into one of a handful of
// user-facing categories, each backed by an `errors.*` i18n key. Pages show
// `$_(apiErrorKey(err))` instead of the raw axios message ("Request failed
// with status code 404"), which is neither translated nor meaningful.

export type ApiErrorKind =
  | "network"
  | "timeout"
  | "unauthorized"
  | "forbidden"
  | "not_found"
  | "server"
  | "unknown";

interface ErrorShape {
  code?: unknown;
  status?: unknown;
  request?: unknown;
  response?: { status?: unknown } | null;
}

function asShape(err: unknown): ErrorShape {
  return typeof err === "object" && err !== null ? (err as ErrorShape) : {};
}

function statusOf(err: ErrorShape): number | null {
  const candidates = [err.response?.status, err.status];
  for (const c of candidates) {
    const n = typeof c === "number" ? c : Number(c);
    if (Number.isFinite(n) && n > 0) return n;
  }
  return null;
}

export function classifyApiError(err: unknown): ApiErrorKind {
  const e = asShape(err);

  // axios sets these codes before any response exists.
  if (e.code === "ECONNABORTED" || e.code === "ETIMEDOUT") return "timeout";
  if (e.code === "ERR_NETWORK") return "network";

  const status = statusOf(e);
  if (status === null) {
    // A request object with no response is axios's "the server never
    // answered" shape; anything else we simply do not understand.
    return e.request !== undefined && e.response == null ? "network" : "unknown";
  }
  if (status === 401) return "unauthorized";
  if (status === 403) return "forbidden";
  if (status === 404) return "not_found";
  if (status >= 500) return "server";
  return "unknown";
}

/** The i18n key for the friendly message that describes `err`. */
export function apiErrorKey(err: unknown): string {
  return `errors.${classifyApiError(err)}`;
}
