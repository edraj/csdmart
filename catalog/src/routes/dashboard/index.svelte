<script lang="ts">
    import { onMount } from "svelte";
    import { goto } from "@roxi/routify";
    import { get } from "svelte/store";
    import { roles } from "@/stores/user";
    import { canAccessAdminArea } from "@/lib/access";

    $goto;

    // Role-aware landing: admins go to the admin dashboard, everyone else to
    // their profile. Without this, a non-admin bounced out of /dashboard/admin
    // would be redirected straight back here and loop.
    onMount(() => {
        $goto(canAccessAdminArea(get(roles)) ? "/dashboard/admin" : "/me");
    });
</script>
