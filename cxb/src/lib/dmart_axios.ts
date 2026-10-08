/**
 * The single axios instance every cxb route shares, installed into the tsdmart
 * SDK via Dmart.setAxiosInstance.
 *
 * This used to live in the script block of routes/management/_module.svelte,
 * which meant it only existed once that lazily-loaded layout had run. Routes
 * outside /management — the password-reset pages, which must be reachable
 * while signed out — then found Dmart.axiosDmartInstance undefined on a direct
 * load or a refresh, and every request died as a swallowed TypeError.
 *
 * Authentication is cookie-only: the server sets an HttpOnly `auth_token`
 * cookie at login and accepts it on every route (same-origin requests pass its
 * CSRF gate via Fetch Metadata), so the instance sends credentials and no
 * bearer token is kept in web storage. Two conditions in the response
 * interceptor are load-bearing and must not be widened:
 *
 *   - `[47, 48, 49]` only. Account lockout (code 110) also arrives as HTTP
 *     401; reacting to it would sign the user out spuriously.
 *   - a locally signed-in user only. Without it, an expected 401 on a
 *     password-reset page (where the visitor has no session at all) would
 *     reload the page out from under the form.
 *
 * `isRedirectingToLogin` is module scope, so the latch now spans the whole app
 * rather than one layout instance — a strict improvement over the old
 * per-layout flag.
 *
 * Config ordering: src/main.ts awaits `configReady` before mounting App, so
 * every module reached through the component tree — including whoever calls
 * ensureDmartAxios() — observes the populated `website`. The call is still
 * lazy and idempotent so the first caller wins and later callers are no-ops.
 */
import axios, { type AxiosInstance } from "axios";
import { Dmart } from "@edraj/tsdmart";
import { website } from "@/config";
import { resolveAxiosBaseUrl } from "@shared/backend-url";
import { Level } from "@/utils/toast";
import { debouncedShowToast } from "@/utils/debounce";

let instance: AxiosInstance | null = null;
let isRedirectingToLogin = false;

// Keys the SDK and the user store write for a session. "authToken" is the
// pre-cookie-only token key: never written any more, still removed so an
// upgraded browser does not keep a token on disk.
const SESSION_KEYS = ["user", "permissions", "roles", "authToken"];

/**
 * Whether this browser believes it has a session, read from the persisted user
 * record (which holds no secret). The server is the authority — this only
 * decides whether a 401 is a session expiry worth reacting to.
 */
export function hasLocalSession(): boolean {
  if (typeof localStorage === "undefined") return false;
  try {
    const stored = JSON.parse(localStorage.getItem("user") || "null");
    return stored?.signedin === true;
  } catch {
    return false;
  }
}

/** Forget the local session so the layouts render the Login form. */
export function clearLocalSession(): void {
  if (typeof localStorage === "undefined") return;
  for (const key of SESSION_KEYS) localStorage.removeItem(key);
}

export function ensureDmartAxios(): AxiosInstance {
  if (instance) return instance;

  const dmartAxios = axios.create({
    // Not `website.backend` directly: it is blank for a same-origin deployment,
    // and axios reads a blank base as "resolve against the document URL" — which
    // under <base href="/cxb/"> sends `user/login` to /cxb/<current route>/user/login.
    baseURL: resolveAxiosBaseUrl(website.backend, window.location.origin),
    withCredentials: true,
    timeout: website.backend_timeout,
  });

  // No request interceptor: the auth_token cookie travels with every request
  // thanks to withCredentials, and the SDK's own `headers` object carries the
  // bearer token for the page that just logged in.
  dmartAxios.interceptors.response.use(
    (request) => {
      return request;
    },
    (error) => {
      if (error.code === "ERR_NETWORK") {
        debouncedShowToast(
          Level.warn,
          "Network error.\nPlease check your connection or the server is down.",
        );
      }
      if (
        error.response?.status === 401 &&
        [47, 48, 49].includes(error.response?.data?.error?.code) &&
        !isRedirectingToLogin &&
        hasLocalSession()
      ) {
        isRedirectingToLogin = true;
        clearLocalSession();
        window.location.reload();
      }
      return Promise.reject(error);
    },
  );

  Dmart.setAxiosInstance(dmartAxios as any);
  instance = dmartAxios;
  return dmartAxios;
}
