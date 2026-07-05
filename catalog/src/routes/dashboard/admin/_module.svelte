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

<slot />
