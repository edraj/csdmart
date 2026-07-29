<script lang="ts">
  import DashboardHeader from "@/components/DashboardHeader.svelte";
  import { signout, user } from "@/stores/user";
  import { onMount } from "svelte";
  import { Dmart } from "@edraj/tsdmart";
  import { website } from "@/config";
  import axios from "axios";
  import { get } from "svelte/store";
  import { initGlobalWebSocket } from "@/stores/websocket";
  import { isPublicRoute } from "@/lib/constants";
  import { withBasePrefix } from "@/lib/basePath";

  function redirectTo(path: string) {
    const target = withBasePrefix(path);
    if (window.location.pathname !== target) {
      window.location.href = target;
    }
  }

  const dmartAxios = axios.create({
    baseURL: website.backend,
    withCredentials: true,
    timeout: 30000,
  });

  // Add request interceptor to inject auth token
  dmartAxios.interceptors.request.use(
    (config) => {
      const token = localStorage.getItem("authToken");
      if (token) {
        config.headers.Authorization = `Bearer ${token}`;
      }
      return config;
    },
    (error) => Promise.reject(error),
  );

  dmartAxios.interceptors.response.use(
    (res) => res,
    async (error) => {
      if (error.code === "ERR_NETWORK") {
        console.warn("Network error: Check connection or server.");
      }

      const errorCode = error.response?.data?.error?.code;
      if (error.response?.status === 401 && [47, 48, 49].includes(errorCode)) {
        const currentPath = window.location.pathname;
        if (!isPublicRoute(currentPath)) {
          console.log(`401 Unauthorized (code ${errorCode}) - redirecting to login`);
          redirectTo("/login");
        }
        await signout();
      }

      return Promise.reject(error);
    },
  );

  Dmart.setAxiosInstance(dmartAxios as any);

  onMount(async () => {
    const currentPath = window.location.pathname;

    if (isPublicRoute(currentPath)) {
      return;
    }

    // Session probe. /info/me is gone (the whole /info group is super_admin
    // now), so this asks /user/profile — which, unlike /info/me, requires
    // authentication. That loses the "200 with authenticated:false for
    // anonymous callers" property the old probe was chosen for, so answer the
    // anonymous case locally instead: no stored token means signed out, and
    // there is nothing to ask the server about. Keeps cold loads free of the
    // 401 the old comment cared about, and skips a round-trip.
    //
    // With a token, getProfile is strictly more useful than the old probe: it
    // both validates the session AND repopulates the roles/permissions
    // localStorage that syncRolesFromStorage/syncPermissionsFromStorage read
    // (tsdmart writes them as a side effect). A failure here means the token
    // is expired or revoked, which is exactly the signed-out path.
    const storedToken =
      typeof localStorage !== "undefined" && localStorage.getItem("authToken");
    if (!storedToken) {
      await signout();
      redirectTo("/login");
      return;
    }

    try {
      const r = await Dmart.getProfile();

      if (r?.status !== "success" || !r?.records?.length) {
        await signout();
        redirectTo("/login");
        return;
      }


      // Connect global WebSocket for real-time notifications and chat.
      // Skipped when enable_websocket is explicitly false in config.json,
      // which keeps getWebSocketService() returning null so all WS-using
      // call sites (messaging page, sendChatMessage, etc.) become no-ops.
      const token = localStorage.getItem("authToken");
      const shortname = get(user).shortname;
      if (token && website.enable_websocket !== false) {
        initGlobalWebSocket(token, shortname);
      }

      if (currentPath === "/" || currentPath === "/login") {
        // Land on the generic /dashboard, which routes by role
        // (admins -> /dashboard/admin, others -> /me). Avoids sending
        // non-admins through the guarded admin subtree on every login.
        redirectTo("/dashboard");
      }
    } catch (error: any) {
      // Expired/revoked token, or the server is unreachable. Both end the
      // same way for a route that requires a session: sign out and show the
      // login form. (A network blip therefore costs the user their local
      // session — same as before this migration, when a failed /info/me was
      // treated identically.)
      console.warn("Session probe failed:", error?.message ?? error);
      await signout();
      redirectTo("/login");
    }
  });
</script>

<div class="app-shell">
  <DashboardHeader />
  <main class="app-main">
    <slot />
  </main>
</div>

<style>
  .app-shell {
    display: flex;
    flex-direction: column;
    min-height: 100vh;
    background: var(--gradient-page);
  }

  .app-main {
    flex: 1;
    animation: fadeIn var(--duration-normal) var(--ease-out);
  }

</style>
