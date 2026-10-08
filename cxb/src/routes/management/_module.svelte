<!-- routify:meta reset -->
<script lang="ts">
    import { Dmart, DmartScope } from "@edraj/tsdmart";
    import { clearLocalSession, ensureDmartAxios } from "@/lib/dmart_axios";
    import Login from "@/components/Login.svelte";
    import ManagementHeader from "@/components/management/ManagementHeader.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { getSpaces } from "@/lib/dmart_services";
    import { user } from "@/stores/user";


    // The axios instance (and its 401 interceptor) now lives in
    // src/lib/dmart_axios.ts so routes outside /management — the
    // password-reset pages — have a working SDK too. Idempotent: the root
    // layout has normally created it already.
    ensureDmartAxios();

    // Cookie-only auth: the HttpOnly auth_token cookie the server set at login
    // authenticates every request, so no bearer token is kept in web storage
    // and none is handed to the SDK here. The SDK picks `public/*` for reads
    // whenever it holds no token, which would be wrong for this admin UI —
    // every route under /management is a signed-in route — so pin its default
    // scope to managed. Callers that pass a scope explicitly are unaffected.
    // (The SDK types the method private; it is a plain static the SDK itself
    // calls through `Dmart.defaultScope()`, so replacing it is effective.)
    (Dmart as unknown as { defaultScope: () => DmartScope }).defaultScope =
        () => DmartScope.managed;

    // Boot session probe: GET /user/profile is the authoritative session
    // check — it returns the caller's user record (and the SDK caches roles /
    // permissions in localStorage) when signed in, and fails otherwise.
    // Mid-session expiration is still detected by the response interceptor
    // in src/lib/dmart_axios.ts when a regular API call returns 401.
    //
    // No local session means signed out, and the browser already knows that —
    // so answer locally rather than asking the server. /info/me used to be
    // AllowAnonymous precisely so a cold load wouldn't paint a 401 in the
    // console; /user/profile has no such branch, and without this check every
    // anonymous visit would fire a request whose only possible answer is 401.
    // It also keeps anonymous callers off the 401 interceptor, which reloads
    // the page.
    const probe = $user?.signedin
        ? Dmart.getProfile()
        : Promise.reject(new Error("no local session"));

    let phase = $state<"pending" | "authed" | "anon">("pending");

    void probe.then((r) => {
        // Both checks are load-bearing: getProfile REJECTS on a transport or
        // auth failure, and RESOLVES with a non-success envelope when the
        // server answered but refused. Dropping either lets one of those two
        // reach the {:then} branch as if the session were live.
        if (r?.status !== "success" || !r?.records?.length) {
            throw new Error("not signed in");
        }
        // Authed — fire the spaces fetch (best-effort) and resolve.
        getSpaces().catch(() => {});
        phase = "authed";
        return r;
    }).catch(() => {
        // Anonymous or expired session — clean up any stale local state so
        // the Login form shows. permissions/roles are written by the SDK as a
        // side effect of getProfile and must go with the rest: stale privilege
        // data outliving the session is what drives the next user's UI gating.
        clearLocalSession();
        user.set({ signedin: false, locale: $user?.locale });
        phase = "anon";
    });
</script>

{#if phase === "pending"}
    <div class="flex w-full h-svh justify-center items-center">
        <LoadingState />
    </div>
{:else if phase === "anon"}
    <Login />
{/if}
<!-- The child route is rendered exactly ONCE, here, and only revealed when
     the session is live. Routify expects the parent of an active child route
     to put its children in the DOM within 5s of navigation, so they cannot
     wait for the probe; but rendering them hidden during the probe AND again
     in the signed-in branch (as this used to) mounted every page twice on a
     cold load and sent every request twice. -->
<div class="flex flex-col h-screen bg-surface text-text" hidden={phase !== "authed"}>
    {#if phase === "authed"}
        <ManagementHeader />
    {/if}
    <div class="flex-grow overflow-auto">
        <!-- Routify 3.6 renders route components with `let:` directives
             (RenderFragment.svelte), so the child route arrives as a Svelte 4 slot;
             a layout that renders {@render children()} throws
             invalid_default_snippet at runtime. svelte-check flags the slot as
             deprecated; ESLint's compile pass does not, hence both comments. -->
        <!-- eslint-disable-next-line svelte/no-unused-svelte-ignore -- only svelte-check emits slot_element_deprecated here -->
        <!-- svelte-ignore slot_element_deprecated -->
        <slot />
    </div>
</div>
