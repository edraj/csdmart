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

    // Boot fires before we know whether the user is signed in; the server
    // 401s if not. Swallow rejections here so they don't surface as
    // unhandled-promise console errors — the result is read post-login
    // anyway and the await block below handles getProfile()'s rejection.
    getSpaces().catch(() => {});

    const profilePromise = Dmart.getProfile();
</script>

{#await profilePromise}
    <div class="flex w-svw h-svh justify-center items-center">
        <Spinner color="blue" size="16" />
    </div>
{:then _}
    {#if !$user || !$user.signedin}
        <Login />
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
{/await}
