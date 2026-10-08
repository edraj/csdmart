<script lang="ts">
    import { onMount } from "svelte";
    import { goto as gotoStore } from "@roxi/routify";
    import { get } from "svelte/store";
    import { permissions } from "@/stores/permissions";
    import { canAccessAdminSection } from "@/lib/access";

    // Routify's helpers read the fragment context when first subscribed, and
    // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
    // first touched inside an async callback logs "Unable to access context".
    // Capture the navigate function once, during component init.
    const goto = $gotoStore;

    // Landing redirect: admins go to the admin dashboard, everyone else to
    // their profile. Uses the SAME permission-based predicate as
    // guardAdminArea — if the two disagreed (e.g. roles say admin but the
    // permissions map doesn't), /dashboard and /dashboard/admin would bounce
    // the user between each other in an endless full-reload loop.
    onMount(() => {
        goto(canAccessAdminSection(get(permissions)) ? "/dashboard/admin" : "/me");
    });
</script>
