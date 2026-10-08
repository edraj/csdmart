import {writable} from "svelte/store";

/**
 * The access token for the current tab, in memory only.
 *
 * The server sets an HttpOnly `auth_token` cookie at login and accepts it on
 * every route and on the WebSocket handshake, so cxb authenticates with the
 * cookie alone and nothing about the session is written to web storage. This
 * store holds the token only for the lifetime of the page that logged in; a
 * reload starts from "" and the cookie carries the session from there.
 *
 * A stale copy may still sit in localStorage under "authToken" from before this
 * change — signout and the 401 interceptor remove it.
 */
export const authToken = writable("");
