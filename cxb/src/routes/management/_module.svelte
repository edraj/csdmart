<!-- routify:meta reset -->
<script>
    import {goto} from '@roxi/routify';
    import {Dmart} from "@edraj/tsdmart";
    import {ensureDmartAxios} from "@/lib/dmart_axios";
    import Login from "@/components/Login.svelte";
    import ManagementHeader from "@/components/management/ManagementHeader.svelte";
    import {Spinner} from "flowbite-svelte";
    import {getSpaces} from "@/lib/dmart_services.js";
    import {onMount} from "svelte";
    import {user} from "@/stores/user.js";

    $goto

    // The axios instance (and its 401 interceptor) now lives in
    // src/lib/dmart_axios.ts so routes outside /management — the
    // password-reset pages — have a working SDK too. Idempotent: the root
    // layout has normally created it already.
    ensureDmartAxios();

    const storedToken = typeof localStorage !== 'undefined' && localStorage.getItem("authToken");
    if (storedToken) {
        Dmart.setToken(storedToken);
    }

    // Boot session probe: GET /user/profile is the authoritative session
    // check — it returns the caller's user record (and the SDK caches roles /
    // permissions in localStorage) when signed in, and fails otherwise.
    // Mid-session expiration is still detected by the response interceptor
    // in src/lib/dmart_axios.ts when a regular API call returns 401.
    //
    // No token means signed out, and the browser already knows that — so
    // answer locally rather than asking the server. /info/me used to be
    // AllowAnonymous precisely so a cold load wouldn't paint a 401 in the
    // console; /user/profile has no such branch, and without this check every
    // anonymous visit would fire a request whose only possible answer is 401.
    // It also keeps anonymous callers off the 401 interceptor, which reloads
    // the page.
    const probe = storedToken
        ? Dmart.getProfile()
        : Promise.reject(new Error("no stored token"));

    const profilePromise = probe.then((r) => {
        // Both checks are load-bearing: getProfile REJECTS on a transport or
        // auth failure, and RESOLVES with a non-success envelope when the
        // server answered but refused. Dropping either lets one of those two
        // reach the {:then} branch as if the session were live.
        if (r?.status !== "success" || !r?.records?.length) {
            throw new Error("not signed in");
        }
        // Authed — fire the spaces fetch (best-effort) and resolve.
        getSpaces().catch(() => {});
        return r;
    }).catch((error) => {
        // Anonymous or expired session — clean up any stale local state so
        // the Login form shows. permissions/roles are written by the SDK as a
        // side effect of getProfile and must go with the rest: stale privilege
        // data outliving the session is what drives the next user's UI gating.
        if (typeof localStorage !== "undefined") {
            localStorage.removeItem("authToken");
            localStorage.removeItem("user");
            localStorage.removeItem("permissions");
            localStorage.removeItem("roles");
        }
        user.set({signedin: false, locale: $user?.locale});
        throw error;
    });
</script>

{#await profilePromise}
    <div class="flex w-svw h-svh justify-center items-center">
        <Spinner color="blue" size="16" />
    </div>
    <!-- Routify expects the parent of an active child route to put a
         <slot /> in the DOM within 5s of navigation. While we're still
         resolving auth (or showing Login), the slot would otherwise be
         absent and Routify logs "Failed to render index within 5s".
         Render it hidden so the timer is satisfied; the child mounts
         silently and gets revealed once the user signs in. Boot 401s
         from this early mount are silenced by per-callsite log gating;
         the session probe itself no longer contributes one, because it
         skips the request entirely when there is no stored token. -->
    <div style="display:none"><slot /></div>
{:then _}
    {#if !$user || !$user.signedin}
        <Login />
        <div style="display:none"><slot /></div>
    {:else}
        <div class="flex flex-col h-screen">
            <ManagementHeader />
            <div class="flex-grow overflow-auto">
                <slot />
            </div>
        </div>
    {/if}
{:catch error}
    <Login />
    <div style="display:none"><slot /></div>
{/await}
