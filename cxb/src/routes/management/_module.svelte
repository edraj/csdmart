<!-- routify:meta reset -->
<script>
    import {goto} from '@roxi/routify';
    import {Dmart} from "@edraj/tsdmart";
    import {website} from "@/config";
    import axios from "axios";
    import Login from "@/components/Login.svelte";
    import ManagementHeader from "@/components/management/ManagementHeader.svelte";
    import {Level} from "@/utils/toast";
    import {debouncedShowToast} from "@/utils/debounce";
    import {Spinner} from "flowbite-svelte";
    import {getSpaces} from "@/lib/dmart_services.js";
    import {onMount} from "svelte";
    import {user} from "@/stores/user.js";

    $goto

    const dmartAxios = axios.create({
        baseURL: website.backend,
        withCredentials: true,
        timeout: website.backend_timeout,
    });
    let isRedirectingToLogin = false;
    dmartAxios.interceptors.response.use((request) => {
        return request;
    }, (error) => {
        if(error.code === 'ERR_NETWORK'){
            debouncedShowToast(Level.warn, "Network error.\nPlease check your connection or the server is down.");
        }
        if (
            error.response?.status === 401
            && [47, 48, 49].includes(error.response?.data?.error?.code)
            && !isRedirectingToLogin
            && localStorage.getItem("authToken")
        ) {
            isRedirectingToLogin = true;
            localStorage.removeItem("authToken");
            localStorage.removeItem("user");
            window.location.reload();
        }
        return Promise.reject(error);
    });
    Dmart.setAxiosInstance(dmartAxios);

    const storedToken = typeof localStorage !== 'undefined' && localStorage.getItem("authToken");
    if (storedToken) {
        Dmart.setToken(storedToken);
    }

    // Boot session probe: GET /user/profile is the authoritative session
    // check — it returns the caller's user record (and the SDK caches roles /
    // permissions in localStorage) when signed in, and fails otherwise.
    // Mid-session expiration is still detected by the response interceptor
    // above when a regular API call returns 401.
    //
    // No token means signed out, and the browser already knows that — so
    // answer locally rather than asking the server. /info/me used to be
    // AllowAnonymous precisely so a cold load wouldn't paint a 401 in the
    // console; /user/profile has no such branch, and without this check every
    // anonymous visit would fire a request whose only possible answer is 401.
    // It also keeps anonymous callers off the 401 interceptor above, which
    // reloads the page.
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
