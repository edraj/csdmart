<script lang="ts">
  /**
   * Field-level render gate. Renders `children` unless the field is listed in
   * the user's `restricted_fields` for (space, subpath, resourceType).
   * The choke-point for hand-written forms; the schema-driven engine
   * (DynamicSchemaBasedForms) applies the same rule in its field loop.
   */
  import type { Snippet } from "svelte";
  import { permissions } from "@/stores/permissions";
  import { isFieldRestricted } from "@/lib/access-fields";

  interface Props {
    field: string;
    space: string;
    subpath: string;
    resourceType: string;
    children: Snippet;
  }

  let { field, space, subpath, resourceType, children }: Props = $props();

  const restricted = $derived(
    isFieldRestricted($permissions, field, space, subpath, resourceType),
  );
</script>

{#if !restricted}
  {@render children()}
{/if}
