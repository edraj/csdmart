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
 * Behaviour is a verbatim move of the old management-layout setup: same
 * baseURL/withCredentials/timeout and the same response interceptor. Two
 * conditions in that interceptor are load-bearing and must not be widened:
 *
 *   - `[47, 48, 49]` only. Account lockout (code 110) also arrives as HTTP
 *     401; reacting to it would sign the user out spuriously.
 *   - a truthy `authToken` only. Without it, an expected 401 on a
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
import { Level } from "@/utils/toast";
import { debouncedShowToast } from "@/utils/debounce";

let instance: AxiosInstance | null = null;
let isRedirectingToLogin = false;

export function ensureDmartAxios(): AxiosInstance {
  if (instance) return instance;

  const dmartAxios = axios.create({
    baseURL: website.backend,
    withCredentials: true,
    timeout: website.backend_timeout,
  });

  // No request interceptor: unlike catalog, cxb never had one — the bearer
  // token is handed to the SDK with Dmart.setToken() from the management
  // layout, which is the only place that needs an authenticated call. Adding
  // one here would change behaviour rather than preserve it.
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
        localStorage.getItem("authToken")
      ) {
        isRedirectingToLogin = true;
        localStorage.removeItem("authToken");
        localStorage.removeItem("user");
        localStorage.removeItem("permissions");
        localStorage.removeItem("roles");
        window.location.reload();
      }
      return Promise.reject(error);
    },
  );

  Dmart.setAxiosInstance(dmartAxios as any);
  instance = dmartAxios;
  return dmartAxios;
}
