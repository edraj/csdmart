<script lang="ts">
  /**
   * Role/permission render gate — the Svelte equivalent of the legacy
   * `<ACL allowedRoles={[...]}>` component.
   *
   * Renders `children` only when the signed-in user satisfies BOTH (when given):
   *   - `allowedRoles` — holds one of these roles (supports the `"all"` wildcard)
   *   - `permission`   — `checkAccess(action, space, subpath, resourceType)` passes
   *
   * Omitting both renders `children` unconditionally. When access is denied,
   * `fallback` is rendered if provided, otherwise nothing.
   */
  import type { Snippet } from "svelte";
  import { roles } from "@/stores/user";
  import { permissions } from "@/stores/permissions";
  import { checkAccess as checkAccessPure, hasAnyRole } from "@/lib/access";

  interface PermissionPredicate {
    action: string;
    space: string;
    subpath: string;
    resourceType: string;
  }

  interface Props {
    allowedRoles?: string[];
    permission?: PermissionPredicate;
    children: Snippet;
    fallback?: Snippet;
  }

  let { allowedRoles, permission, children, fallback }: Props = $props();

  const granted = $derived.by(() => {
    let ok = true;
    if (allowedRoles && allowedRoles.length > 0) {
      ok = hasAnyRole($roles, allowedRoles);
    }
    if (ok && permission) {
      ok = checkAccessPure(
        $permissions,
        permission.action,
        permission.space,
        permission.subpath,
        permission.resourceType,
      );
    }
    return ok;
  });
</script>

{#if granted}
  {@render children()}
{:else if fallback}
  {@render fallback()}
{/if}
