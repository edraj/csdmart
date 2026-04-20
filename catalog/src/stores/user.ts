import { type Writable, writable, get } from "svelte/store";
import {
  type ActionRequestRecord,
  Dmart,
  DmartScope,
  ResourceType,
  type SendOTPRequest,
} from "@edraj/tsdmart";
import { authToken } from "@/stores/auth";
import { getLocaleFromNavigator } from "svelte-i18n";
import { storage } from "@/lib/storage";
import { log } from "@/lib/logger";
import { DEFAULT_ROW_PER_PAGE } from "@/lib/constants";
import { teardownGlobalWebSocket } from "@/stores/websocket";
import { resolveAutoShortname } from "@/lib/helpers";

enum Locale {
  ar = "ar",
  en = "en",
}

export interface User {
  signedin: boolean;
  locale: Locale;
  shortname?: string;
  localized_displayname?: string;
  account?: Record<string, any>;
}

const KEY = "user";

const fallback_locale = Locale.ar;
/**
 * Guesses the user's preferred locale based on browser navigator language
 * @returns The guessed locale or fallback locale if not found
 */
function guess_locale(): Locale {
  const _locale = getLocaleFromNavigator();

  if (_locale && _locale in Locale) {
    return Locale[_locale as keyof typeof Locale];
  }

  return fallback_locale;
}

let signedout: User = { signedin: false, locale: guess_locale() };
export let user: Writable<User>;

export let roles: Writable<string[]> = writable(storage.getJson("roles", []));

// Load the user information from store, if it exists
user = writable<User>(storage.getJson(KEY, signedout));

/**
 * Handles successful login response: sets auth token, user state, and localStorage
 */
async function handleLoginResponse(response: { status: string; records: any[] }) {
  if (response.status === "success" && response.records.length > 0) {
    const account = response.records[0];
    const auth = account.attributes.access_token;
    authToken.set(auth);
    storage.set("authToken", auth);

    const _user: User = {
      signedin: true,
      locale: guess_locale(),
      shortname: account.shortname,
      localized_displayname: account.attributes?.displayname?.en,
      account: account,
    };
    user.set(_user);
    storage.setJson(KEY, _user);
    storage.set("rowPerPage", DEFAULT_ROW_PER_PAGE);

    // Populate roles/permissions localStorage (tsdmart writes them as a
    // side effect of getProfile), then sync the roles store so the UI
    // reflects the new session without waiting for a page reload.
    try {
      await Dmart.getProfile();
    } catch (error) {
      log.error("Error refreshing profile after login:", error);
    }
    syncRolesFromStorage();
  } else {
    user.set(signedout);
    storage.setJson(KEY, signedout);
    roles.set([]);
  }
}

// dmart's server applies a 10 req/min/IP rate limit to login + OTP endpoints;
// this client-side throttle protects against the UI firing duplicates (impatient
// double-clicks, programmatic retries) before we hit that server ceiling.
const AUTH_THROTTLE_MS = 2000;
let _lastAuthAttempt = 0;

function throttleAuth(): void {
  const now = Date.now();
  if (now - _lastAuthAttempt < AUTH_THROTTLE_MS) {
    throw new Error("Please wait a moment before trying again.");
  }
  _lastAuthAttempt = now;
}

/**
 * Signs in a user with username and password
 * @param username - The username to sign in with
 * @param password - The password for authentication
 */
export async function signin(username: string, password: string) {
  throttleAuth();
  const response = await Dmart.login(username, password);
  await handleLoginResponse(response);
}

/**
 * Signs in a user with email and password
 * @param email - The email to sign in with
 * @param password - The password for authentication
 */
export async function loginBy(email: string, password: string) {
  throttleAuth();
  const response = await Dmart.loginBy({ email: email }, password);
  await handleLoginResponse(response);
}

export async function requestOtp(email: string): Promise<string> {
  throttleAuth();
  const request: SendOTPRequest = { email: email };
  try {
    const response = await Dmart.otpRequest(request);
    if (response.status === "success") {
      return response.records?.[0]?.attributes?.request_id ?? "";
    } else {
      throw new Error(response.error?.message || "OTP request failed");
    }
  } catch (error: any) {
    if (error.response?.data?.error?.message) {
      throw new Error(error.response.data.error.message);
    } else if (error.message) {
      throw new Error(error.message);
    } else {
      throw new Error("OTP request failed. Please try again.");
    }
  }
}

export async function checkExisting(
  prop: string,
  value: string
): Promise<boolean> {
  try {
    const response = await Dmart.checkExisting(prop, value);
    return (response as any).attributes.unique;
  } catch (error: any) {
    if (error.response?.data?.error?.message) {
      throw new Error(error.response.data.error.message);
    } else if (error.message) {
      throw new Error(error.message);
    } else {
      throw new Error("Check existing failed. Please try again.");
    }
  }
}

export async function register(
  email: string,
  otp: string,
  password: string,
  confirmPassword: string,
  role: string,
  data: any
) {
  if (password !== confirmPassword) {
    throw new Error("Passwords do not match");
  }

  const attributes: Record<string, any> = {
    email: email,
    email_otp: otp,
    password: password,
    roles: [role],
    description: { en: data.description },
    payload: {
      content_type: "json",
      body: data,
    },
  };
  const { shortname } = resolveAutoShortname("auto", attributes);

  const request: ActionRequestRecord = {
    resource_type: ResourceType.user,
    shortname,
    subpath: "/",
    attributes,
  };

  try {
    const response = await Dmart.createUser(request);

    if (response.status === "success") {
      await loginBy(email, password);
    } else {
      throw new Error(response.error?.message || "Registration failed");
    }

    return response;
  } catch (error: any) {
    if (error.response?.data?.error?.message) {
      throw new Error(error.response.data.error.message);
    } else if (error.message) {
      throw new Error(error.message);
    } else {
      throw new Error("Registration failed. Please try again.");
    }
  }
}

/**
 * Returns DmartScope.managed if user is signed in, DmartScope.public otherwise.
 * Works in both Svelte components and plain TypeScript files.
 */
export function getCurrentScope(): DmartScope {
  return get(user)?.signedin ? DmartScope.managed : DmartScope.public;
}

export async function signout() {
  if (!storage.getJson<User | null>(KEY, null)?.signedin) return;

  // Capture the token for the server logout call, then clear local state
  // BEFORE hitting the server. If /user/logout itself returns 401 with code
  // 47/48/49, the axios response interceptor calls signout() again — the
  // early-return guard above must see a cleared `signedin` flag or we get an
  // infinite recursion. Clearing first also means we never end up in a
  // "stuck" state where the server call failed but localStorage still says
  // we're signed in.
  teardownGlobalWebSocket();
  storage.remove("rowPerPage");
  storage.remove("authToken");
  storage.remove("roles");
  storage.remove("permissions");
  authToken.set("");
  roles.set([]);
  user.set(signedout);
  storage.remove(KEY);

  try {
    await Dmart.logout();
  } catch (error) {
    log.error("Error during server logout:", error);
  }
}

/**
 * Syncs the roles store from the latest profile payload.
 * Called by getProfile() after the server refreshes localStorage.
 */
export function syncRolesFromStorage() {
  roles.set(storage.getJson<string[]>("roles", []));
}

export function switchLocale(locale: Locale) {
  user.update((currentUser) => {
    const updatedUser = { ...currentUser, locale };
    storage.setJson(KEY, updatedUser);
    return updatedUser;
  });
}

export async function contactUs(
  name: string,
  email: string,
  message: string,
  subject: string
) {
  try {
    const response = await Dmart.submit({
      spaceName: "applications",
      schemaShortname: "contact",
      subpath: "contacts",
      record: {
        full_name: name,
        email: email,
        message: message,
        subject: subject,
      },
      resourceType: ResourceType.content,
    });
    if (response.status === "success") {
      return response;
    } else {
      throw new Error(response.error?.message || "Registration failed");
    }
  } catch (error: any) {
    if (error.response?.data?.error?.message) {
      throw new Error(error.response.data.error.message);
    } else if (error.message) {
      throw new Error(error.message);
    } else {
      throw new Error("Sending message failed. Please try again.");
    }
  }
}
