import {type Writable, writable} from "svelte/store";
import {Dmart} from "@edraj/tsdmart";
import {authToken} from "@/stores/auth";
import {getLocaleFromNavigator} from "svelte-i18n";

enum Locale {
  ar = "ar",
  en = "en",
}

export interface User {
  signedin: boolean;
  locale: Locale;
  shortname?: string;
  localized_displayname?: string;
  account?: Record<string, unknown>;
}

const KEY = "user";
// Pre-cookie-only builds persisted the bearer token under this key. It is never
// written any more; it is only removed so an upgraded browser does not keep a
// token on disk.
const LEGACY_TOKEN_KEY = "authToken";

const fallback_locale = Locale.ar;
function guess_locale(): Locale {
  const _locale = getLocaleFromNavigator();

  if (_locale && _locale in Locale) {
    return Locale[_locale];
  }

  return fallback_locale;
}

const signedout: User = { signedin: false, locale: guess_locale() };

// Load the user information from store, if it exists
let initialUser: User = signedout;
if (typeof localStorage !== "undefined") {
    try {
        const data = localStorage.getItem(KEY);
        if (data) {
            initialUser = JSON.parse(data) || signedout;
        }
    } catch {
        // Corrupted localStorage data, fall back to signedout state
        initialUser = signedout;
    }
}
export const user: Writable<User> = writable<User>(initialUser);

export async function signin(username: string, password: string) {
  const response = await Dmart.login(username, password);

  if (response.status === "success" && response.records.length > 0) {
    const account = response.records[0];
    // In memory only for this page. The HttpOnly cookie the server set on the
    // same response is what authenticates every later request, including the
    // ones made after the reload that follows login.
    authToken.set(account.attributes.access_token ?? "");

    // The login record carries the token inside `attributes`; the persisted
    // copy of the account must not.
    const { access_token: _access, refresh_token: _refresh, ...safeAttributes } =
      (account.attributes ?? {}) as Record<string, unknown>;
    const _user: User = {
      signedin: true,
      locale: guess_locale(),
      shortname: account.shortname,
      localized_displayname: account.attributes?.displayname?.en,
      account: { ...account, attributes: safeAttributes },
    };
    user.set(_user);
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(KEY, JSON.stringify(_user));
      localStorage.setItem("rowPerPage", "15");
      localStorage.removeItem(LEGACY_TOKEN_KEY);
    }
  } else {
    user.set(signedout);
    if (typeof localStorage !== 'undefined')
      localStorage.setItem(KEY, JSON.stringify(signedout));
  }
}

export async function signout() {
  if (typeof localStorage === 'undefined') return;
  try {
    const stored = JSON.parse(localStorage.getItem(KEY) || "null");
    if (stored?.signedin) {
      try {
        await Dmart.logout();
      } catch {
        // Local signout should still clear browser state when the server is unreachable.
      }
    }
  } finally {
    localStorage.removeItem(KEY);
    localStorage.removeItem("rowPerPage");
    localStorage.removeItem(LEGACY_TOKEN_KEY);
    // Written by the SDK's getProfile(); stale privilege data must not
    // outlive the session or it drives the next user's UI gating.
    localStorage.removeItem("permissions");
    localStorage.removeItem("roles");
    authToken.set("");
    user.set(signedout);
  }
}
