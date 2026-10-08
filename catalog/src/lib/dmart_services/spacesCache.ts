// The one spaces cache (review perf #11).
//
// getSpaces() used to run the 100-record spaces query on every call, and the
// admin, template, role and permission pages — plus getSpaceHideFolders(),
// which every folder navigation awaits — each called it again. One promise
// per scope here, kept for the session and dropped when a space is created,
// edited or deleted, when the visitor signs out, or after a short TTL so a
// change made from another client is picked up on the next navigation.
//
//   const response = await getSpacesCached(DmartScope.managed);
//   invalidateSpacesCache();   // after createSpace / editSpace / deleteSpace
//
// The factory is exported so the TTL and invalidation rules are unit-tested
// with a fake fetcher (spacesCache.test.ts).

import { Dmart, DmartScope, QueryType, type ApiQueryResponse } from "@edraj/tsdmart";
import { MANAGEMENT_SPACE } from "@/lib/constants";

export const SPACES_CACHE_TTL_MS = 5 * 60 * 1000;

export type SpacesFetcher = (scope: DmartScope) => Promise<ApiQueryResponse>;

interface CacheEntry {
  at: number;
  promise: Promise<ApiQueryResponse>;
}

export interface SpacesCache {
  /** The spaces visible in `scope`; one request per scope until invalidated. */
  get(scope: DmartScope): Promise<ApiQueryResponse>;
  /** Forget everything; the next get() fetches again. */
  invalidate(): void;
  /** Cached scopes (for tests). */
  size(): number;
}

export function createSpacesCache(
  fetch: SpacesFetcher,
  ttlMs: number = SPACES_CACHE_TTL_MS,
  now: () => number = () => Date.now(),
): SpacesCache {
  const entries = new Map<DmartScope, CacheEntry>();

  return {
    get(scope) {
      const hit = entries.get(scope);
      if (hit && now() - hit.at < ttlMs) return hit.promise;

      const promise: Promise<ApiQueryResponse> = fetch(scope).catch((error: unknown) => {
        // A failed lookup must not be served for five minutes.
        if (entries.get(scope)?.promise === promise) entries.delete(scope);
        throw error;
      });
      entries.set(scope, { at: now(), promise });
      return promise;
    },
    invalidate() {
      entries.clear();
    },
    size() {
      return entries.size;
    },
  };
}

async function querySpaces(scope: DmartScope): Promise<ApiQueryResponse> {
  const response = await Dmart.query(
    {
      type: QueryType.spaces,
      space_name: MANAGEMENT_SPACE,
      subpath: "/",
      search: "",
      limit: 100,
    },
    scope,
  );
  return response as ApiQueryResponse;
}

const cache = createSpacesCache(querySpaces);

export function getSpacesCached(scope: DmartScope): Promise<ApiQueryResponse> {
  return cache.get(scope);
}

export function invalidateSpacesCache(): void {
  cache.invalidate();
}
