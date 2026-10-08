// A session-wide avatar cache.
//
// The browse pages used to call getAvatar() once per card — and the folder
// page twice per card (review perf #5, #6): N lookups for a page whose 20 posts
// come from three authors. One promise per (scope, owner) here, so a second
// card for the same author, or the same author on the next page, costs nothing.
//
//   const url = await getAvatarCached(owner);              // one owner
//   const urls = await getAvatarsCached(items.map(ownerOf)); // Map<owner, url | null>
//
// A lookup that fails resolves to null and stays cached for the session: for
// an anonymous visitor every protected avatar is a 403, and retrying each one
// on every page is exactly the storm this cache exists to stop. The key
// includes the scope so a visitor who signs in gets fresh (managed) lookups.

import { getCurrentScope } from "@/stores/user";
import { getAvatar } from "./profile";

const cache = new Map<string, Promise<string | null>>();

function cacheKey(shortname: string): string {
  return `${getCurrentScope()}:${shortname}`;
}

export function getAvatarCached(shortname: string | null | undefined): Promise<string | null> {
  if (!shortname) return Promise.resolve(null);
  const key = cacheKey(shortname);
  const hit = cache.get(key);
  if (hit) return hit;
  const pending: Promise<string | null> = Promise.resolve()
    .then(() => getAvatar(shortname))
    .then((url) => url ?? null)
    .catch(() => null);
  cache.set(key, pending);
  return pending;
}

/**
 * Resolve the avatars of a list of owners (duplicates and blanks ignored) in
 * parallel. Never rejects; a missing or failed avatar maps to null.
 */
export async function getAvatarsCached(
  shortnames: Iterable<string | null | undefined>,
): Promise<Map<string, string | null>> {
  const unique = [...new Set([...shortnames].filter((s): s is string => !!s))];
  const urls = await Promise.all(unique.map((s) => getAvatarCached(s)));
  return new Map(unique.map((s, i) => [s, urls[i]]));
}

/** Forget everything (tests, sign-out, a profile picture that just changed). */
export function clearAvatarCache(shortname?: string): void {
  if (shortname === undefined) {
    cache.clear();
    return;
  }
  for (const key of [...cache.keys()]) {
    if (key.endsWith(`:${shortname}`)) cache.delete(key);
  }
}

/** How many lookups are cached (for tests). */
export function avatarCacheSize(): number {
  return cache.size;
}
