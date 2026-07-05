/**
 * Svelte-bound wrapper around the DMART permissions map.
 *
 * `Dmart.getProfile()` writes the resolved permissions to
 * `localStorage["permissions"]` (keyed `"{space}:{subpath}:{rt}"`). This store
 * mirrors that value so the UI can gate per-entry actions/fields reactively —
 * the read-side parallel of the existing `roles` store in `@/stores/user`.
 *
 * NOTE: this module intentionally does NOT import from `@/stores/user`; doing so
 * would form a module-eval cycle (user.ts → permissions.ts → user.ts). For
 * role-reactive checks use `$roles` with the pure helpers in `@/lib/access`.
 */
import {
  type Readable,
  type Writable,
  derived,
  get,
  writable,
} from "svelte/store";
import { storage } from "@/lib/storage";
import {
  checkAccess as checkAccessPure,
  type PermissionsMap,
} from "@/lib/access";

const KEY = "permissions";

export const permissions: Writable<PermissionsMap> = writable(
  storage.getJson<PermissionsMap>(KEY, {}),
);

/** Re-read the permissions map from localStorage (after login / profile refresh). */
export function syncPermissionsFromStorage(): void {
  permissions.set(storage.getJson<PermissionsMap>(KEY, {}));
}

/**
 * Non-reactive permission check for plain-TS call sites.
 * In Svelte markup prefer the reactive {@link can} store.
 */
export function checkAccess(
  action: string,
  space: string,
  subpath: string,
  resourceType: string,
): boolean {
  return checkAccessPure(get(permissions), action, space, subpath, resourceType);
}

/**
 * Reactive permission check for markup:
 *   {#if $can("update", space, subpath, resource_type)} … {/if}
 */
export const can: Readable<
  (action: string, space: string, subpath: string, resourceType: string) => boolean
> = derived(
  permissions,
  ($permissions) =>
    (action: string, space: string, subpath: string, resourceType: string) =>
      checkAccessPure($permissions, action, space, subpath, resourceType),
);
