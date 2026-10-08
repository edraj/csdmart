<script lang="ts">
  import { onMount } from "svelte";
  import { guardAdminArea } from "@/lib/guards";

  // Guards the whole /dashboard/admin/* subtree (index, settings, configs,
  // contact-messages, users, [space_name]/...) via the shared permission-based
  // canAccessAdminSection predicate.
  onMount(() => {
    guardAdminArea();
  });
</script>

<!-- Routify renders layouts with `let:` directives, so this must stay a slot:
     `{@render children()}` throws invalid_default_snippet at runtime (same as
     the root layout and every cxb layout). -->
<!-- eslint-disable-next-line svelte/no-unused-svelte-ignore -- only svelte-check emits slot_element_deprecated here -->
<!-- svelte-ignore slot_element_deprecated -->
<slot />
