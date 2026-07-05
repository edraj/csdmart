<script lang="ts">
    import { onMount } from "svelte";
    import { goto } from "@roxi/routify";
    import { get } from "svelte/store";
    import { permissions } from "@/stores/permissions";
    import { canAccessAdminSection } from "@/lib/access";

    $goto;

    // Landing redirect: admins go to the admin dashboard, everyone else to
    // their profile. Uses the SAME permission-based predicate as
    // guardAdminArea — if the two disagreed (e.g. roles say admin but the
    // permissions map doesn't), /dashboard and /dashboard/admin would bounce
    // the user between each other in an endless full-reload loop.
    onMount(() => {
        $goto(canAccessAdminSection(get(permissions)) ? "/dashboard/admin" : "/me");
    });
</script>
