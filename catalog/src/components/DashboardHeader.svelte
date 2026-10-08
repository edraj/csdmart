<script lang="ts">
  import SearchBar from "./SearchBar.svelte";
  import LanguageMenu from "./LanguageMenu.svelte";
  import ThemeToggle from "./ThemeToggle.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import { onDestroy, tick } from "svelte";
  import { BarsOutline, BellOutline, CloseOutline } from "flowbite-svelte-icons";
  import { newNotificationType } from "@/stores/newNotificationType";
  import { _ } from "@/i18n";
  import { signout, user, type User } from "@/stores/user";
  import { can, permissions } from "@/stores/permissions";
  import { canAccessAdminSection } from "@/lib/access";
  import { ResourceType } from "@edraj/tsdmart";
  import { goto as gotoStore } from "@roxi/routify";
  import { getWebSocketService } from "@/lib/services/websocket";
  import { wsConnected } from "@/stores/websocket";
  import { isPublicRoute } from "@/lib/constants";
  import { website } from "@/config";
  import { stripBase, withBase } from "@/lib/paths";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  $effect(() => {
    const path = stripBase(window.location.pathname);
    if (!$user?.signedin && path !== "/login" && !isPublicRoute(path)) {
      goto("/login");
    }
  });

  let isMenuOpen = $state(false);
  let menuRoot: HTMLDivElement | undefined = $state();
  let menuEl: HTMLDivElement | undefined = $state();
  let triggerEl: HTMLButtonElement | undefined = $state();

  let canSeePermissions = $derived(
    $can("query", "management", "permissions", ResourceType.permission),
  );
  let canSeeRoles = $derived(
    $can("query", "management", "roles", ResourceType.role),
  );
  let canSeeUsers = $derived(
    $can("query", "management", "users", ResourceType.user),
  );
  let canSeeConfigs = $derived(
    $can("query", "management", "configs", ResourceType.content),
  );
  // Shared predicate — must match guardAdminArea and the /dashboard landing
  // redirect, or the menu shows admin links whose pages bounce the user.
  let hasAdminAccess = $derived(canAccessAdminSection($permissions));

  let removeListener: (() => void) | null = null;

  // Register WS listener reactively when connection becomes available
  $effect(() => {
    if ($wsConnected && $user.signedin) {
      const ws = getWebSocketService();
      if (ws && !removeListener) {
        removeListener = ws.addMessageListener(handleRealtimeMessage);
      }
    }
  });

  function handleRealtimeMessage(data: { type?: string; message?: { action_type?: string } }) {
    // csdmart plugin broadcasts arrive as type "notification_subscription"
    // with action_type in the message payload
    if (data.type === "notification_subscription" && data.message?.action_type) {
      const action = data.message.action_type;
      if (action === "create") {
        $newNotificationType = "create_event";
      } else if (action === "update" || action === "progress_ticket") {
        $newNotificationType = "progress";
      }
    }
  }

  onDestroy(() => {
    removeListener?.();
  });

  const notificationTone = $derived($newNotificationType === "progress" ? "text-warning" : "");
  const notificationsLabel = $derived(
    $newNotificationType
      ? `${$_("notifications")} — ${$_("ui.notifications_new")}`
      : $_("notifications"),
  );

  async function handleLogout() {
    closeMenu();
    await signout();
    goto("/login");
  }

  async function openMenu() {
    isMenuOpen = true;
    await tick();
    focusItem(0);
  }

  function closeMenu(returnFocus = false) {
    if (!isMenuOpen) return;
    isMenuOpen = false;
    if (returnFocus) triggerEl?.focus();
  }

  function toggleMenu() {
    if (isMenuOpen) closeMenu();
    else openMenu();
  }

  function menuItems(): HTMLElement[] {
    return menuEl ? Array.from(menuEl.querySelectorAll<HTMLElement>('[role="menuitem"]')) : [];
  }

  function focusItem(index: number) {
    const items = menuItems();
    if (items.length === 0) return;
    items[((index % items.length) + items.length) % items.length]?.focus();
  }

  function onTriggerKeydown(e: KeyboardEvent) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      openMenu();
    }
  }

  function onMenuKeydown(e: KeyboardEvent) {
    const items = menuItems();
    const current = items.indexOf(document.activeElement as HTMLElement);
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        focusItem(current + 1);
        break;
      case "ArrowUp":
        e.preventDefault();
        focusItem(current - 1);
        break;
      case "Home":
        e.preventDefault();
        focusItem(0);
        break;
      case "End":
        e.preventDefault();
        focusItem(items.length - 1);
        break;
      case "Escape":
        e.preventDefault();
        closeMenu(true);
        break;
      case "Tab":
        closeMenu();
        break;
    }
  }

  $effect(() => {
    if (!isMenuOpen) return;
    function handlePointerDown(event: PointerEvent) {
      const target = event.target as Element;
      // The phone drawer's backdrop lives inside menuRoot; a tap on it counts
      // as outside.
      if (menuRoot && (!menuRoot.contains(target) || target.closest(".menu-backdrop"))) {
        closeMenu();
      }
    }
    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  });

  function getInitials(u: User | null | undefined) {
    if (!u) return "?";
    const name = u.localized_displayname || u.shortname || "";
    if (!name) return "?";
    const parts = name.split(" ");
    if (parts.length >= 2) {
      return (parts[0].charAt(0) + parts[1].charAt(0)).toUpperCase();
    }
    return name.substring(0, 2).toUpperCase();
  }

  type MenuEntry = { href: string; label: string; paths: string[]; badge?: boolean };

  const adminEntries = $derived<MenuEntry[]>([
    ...(canSeePermissions
      ? [{ href: "/dashboard/permissions", label: $_("permission"), paths: ["M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z", "M9 12l2 2 4-4"] }]
      : []),
    ...(canSeeRoles
      ? [{ href: "/dashboard/roles", label: $_("roles"), paths: ["M5.121 17.804A9 9 0 1112 21v-1a7 7 0 100-14v1m0 4a3 3 0 013 3 3 3 0 01-3 3 3 3 0 01-3-3 3 3 0 013-3z"] }]
      : []),
    ...(canSeeUsers
      ? [{ href: "/dashboard/admin/users", label: $_("Users"), paths: ["M15 17h5v-1a4 4 0 00-4-4h-1M9 17H4v-1a4 4 0 014-4h1m3-4a3 3 0 11-6 0 3 3 0 016 0zm6 0a3 3 0 11-6 0 3 3 0 016 0z"] }]
      : []),
    { href: "/dashboard/admin/contact-messages", label: $_("contact_messages"), paths: ["M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z"] },
    ...(canSeeConfigs
      ? [
          { href: "/dashboard/admin/configs", label: $_("DefaultRole"), paths: ["M11.983 13.983a2 2 0 100-4 2 2 0 000 4zM19.4 15a1.65 1.65 0 01.33 1.82l-.58 1a1.65 1.65 0 01-1.51.88h-1.12a6.66 6.66 0 01-1.3.76l-.17 1.12a1.65 1.65 0 01-.88 1.51l-1 .58a1.65 1.65 0 01-1.82-.33l-.8-.8a6.66 6.66 0 01-.76-1.3H7.4a1.65 1.65 0 01-1.51-.88l-.58-1a1.65 1.65 0 01.33-1.82l.8-.8a6.66 6.66 0 010-1.52l-.8-.8a1.65 1.65 0 01-.33-1.82l.58-1a1.65 1.65 0 011.51-.88h1.12c.23-.46.49-.89.76-1.3l-.17-1.12a1.65 1.65 0 01.88-1.51l1-.58a1.65 1.65 0 011.82.33l.8.8c.51-.13 1.03-.24 1.52-.24s1.01.11 1.52.24l.8-.8a1.65 1.65 0 011.82-.33l1 .58a1.65 1.65 0 01.88 1.51l-.17 1.12c.46.23.89.49 1.3.76h1.12a1.65 1.65 0 011.51.88l.58 1a1.65 1.65 0 01-.33 1.82l-.8.8c.13.51.24 1.03.24 1.52s-.11 1.01-.24 1.52l.8.8z"] },
          { href: "/dashboard/templates", label: $_("templates._val"), paths: ["M4 5a1 1 0 011-1h14a1 1 0 011 1v2a1 1 0 01-1 1H5a1 1 0 01-1-1V5zM4 13a1 1 0 011-1h6a1 1 0 011 1v6a1 1 0 01-1 1H5a1 1 0 01-1-1v-6zM16 13a1 1 0 011-1h2a1 1 0 011 1v6a1 1 0 01-1 1h-2a1 1 0 01-1-1v-6z"] },
          { href: "/dashboard/reports", label: $_("reports._val"), paths: ["M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2"] },
        ]
      : []),
  ]);

  const mainEntries = $derived<MenuEntry[]>([
    ...(website.enable_public_view
      ? [{ href: "/catalogs", label: $_("Catalog"), paths: ["M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"] }]
      : []),
    ...(website.enable_messaging
      ? [{ href: "/messaging", label: $_("chat"), paths: ["M7 8h10M7 12h6m-9 8l2-4h10a4 4 0 004-4V6a4 4 0 00-4-4H6a4 4 0 00-4 4v10a4 4 0 004 4z"] }]
      : []),
    ...(website.enable_poll
      ? [{ href: "/polls", label: $_("polls._val"), paths: ["M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z"] }]
      : []),
    ...(website.enable_surveys
      ? [{ href: "/surveys", label: $_("surveys._val"), paths: ["M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-3 7h3m-3 4h3m-6-4h.01M9 16h.01"] }]
      : []),
    ...(website.enable_notifications
      ? [{ href: "/notifications", label: $_("notifications"), badge: true, paths: ["M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"] }]
      : []),
    { href: "/me", label: $_("my_profile"), paths: ["M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z"] },
  ]);
</script>

{#snippet menuLink(entry: MenuEntry)}
  <a role="menuitem" href={withBase(entry.href)} class="menu-item" onclick={() => closeMenu()}>
    <svg class="menu-icon" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
      {#each entry.paths as d (d)}
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" {d} />
      {/each}
    </svg>
    <span>{entry.label}</span>
    {#if entry.badge && $newNotificationType}
      <span class="notification-badge" aria-hidden="true"></span>
    {/if}
  </a>
{/snippet}

<header class="site-header" class:signed-in={$user.signedin}>
  <div class="site-header-bar">
    <!-- Logo/Brand -->
    <a href={withBase("/")} class="brand">
      <svg
        class="brand-mark"
        width="32"
        height="32"
        viewBox="8 6 32 32"
        fill="none"
        xmlns="http://www.w3.org/2000/svg"
        aria-hidden="true"
      >
        <g filter="url(#filter0_d_30_2327)">
          <path
            d="M8 16C8 10.4772 12.4772 6 18 6H30C35.5228 6 40 10.4772 40 16V28C40 33.5228 35.5228 38 30 38H18C12.4772 38 8 33.5228 8 28V16Z"
            fill="url(#paint0_linear_30_2327)"
          />
          <path
            d="M18.6667 23.3333C18.5406 23.3338 18.4169 23.2984 18.31 23.2313C18.2032 23.1643 18.1176 23.0682 18.0631 22.9544C18.0087 22.8406 17.9876 22.7137 18.0024 22.5884C18.0172 22.4632 18.0673 22.3446 18.1467 22.2467L24.7467 15.4467C24.7963 15.3895 24.8637 15.3509 24.9381 15.3372C25.0124 15.3234 25.0892 15.3353 25.1559 15.371C25.2226 15.4067 25.2751 15.4639 25.305 15.5334C25.3348 15.6029 25.3401 15.6804 25.3201 15.7533L24.0401 19.7667C24.0023 19.8677 23.9897 19.9764 24.0031 20.0833C24.0166 20.1903 24.0558 20.2925 24.1175 20.381C24.1791 20.4695 24.2613 20.5417 24.3569 20.5914C24.4526 20.6412 24.5589 20.667 24.6667 20.6667H29.3334C29.4596 20.6662 29.5833 20.7016 29.6901 20.7687C29.797 20.8358 29.8826 20.9318 29.937 21.0456C29.9915 21.1594 30.0125 21.2863 29.9977 21.4116C29.9829 21.5369 29.9329 21.6554 29.8534 21.7533L23.2534 28.5533C23.2039 28.6105 23.1364 28.6491 23.0621 28.6629C22.9877 28.6766 22.9109 28.6647 22.8443 28.629C22.7776 28.5933 22.725 28.5361 22.6952 28.4666C22.6654 28.3971 22.66 28.3196 22.6801 28.2467L23.9601 24.2333C23.9978 24.1323 24.0105 24.0237 23.997 23.9167C23.9835 23.8097 23.9443 23.7076 23.8827 23.6191C23.8211 23.5306 23.7389 23.4583 23.6432 23.4086C23.5476 23.3588 23.4412 23.333 23.3334 23.3333H18.6667Z"
            stroke="white"
            stroke-width="1.33333"
            stroke-linecap="round"
            stroke-linejoin="round"
          />
        </g>
        <defs>
          <filter
            id="filter0_d_30_2327"
            x="0"
            y="0"
            width="48"
            height="48"
            filterUnits="userSpaceOnUse"
            color-interpolation-filters="sRGB"
          >
            <feFlood flood-opacity="0" result="BackgroundImageFix" />
            <feColorMatrix
              in="SourceAlpha"
              type="matrix"
              values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0"
              result="hardAlpha"
            />
            <feOffset dy="2" />
            <feGaussianBlur stdDeviation="4" />
            <feComposite in2="hardAlpha" operator="out" />
            <feColorMatrix
              type="matrix"
              values="0 0 0 0 0.388235 0 0 0 0 0.4 0 0 0 0 0.945098 0 0 0 0.2 0"
            />
            <feBlend mode="normal" in2="BackgroundImageFix" result="effect1_dropShadow_30_2327" />
            <feBlend mode="normal" in="SourceGraphic" in2="effect1_dropShadow_30_2327" result="shape" />
          </filter>
          <linearGradient
            id="paint0_linear_30_2327"
            x1="8"
            y1="6"
            x2="40"
            y2="38"
            gradientUnits="userSpaceOnUse"
          >
            <stop stop-color="#6366F1" />
            <stop offset="1" stop-color="#8B5CF6" />
          </linearGradient>
        </defs>
      </svg>
      <span class="brand-name">{$_("Spaces")}</span>
    </a>

    {#if $user.signedin}
      <div class="site-search">
        <SearchBar />
      </div>
    {/if}

    <!-- Controls -->
    <div class="controls">
      <ThemeToggle />
      <LanguageMenu />

      {#if $user.signedin}
        <IconButton label={notificationsLabel} href={withBase("/notifications")} class="relative">
          <BellOutline size="md" class={notificationTone} aria-hidden="true" />
          {#if $newNotificationType}
            <span class="absolute top-1.5 end-1.5 h-2 w-2 rounded-full bg-danger border border-surface-2 animate-pulse" aria-hidden="true"></span>
          {/if}
        </IconButton>
        <span class="sr-only" aria-live="polite">
          {$newNotificationType ? $_("ui.notifications_new") : ""}
        </span>

        <!-- Main menu -->
        <div class="menu-container" bind:this={menuRoot}>
          <button
            bind:this={triggerEl}
            type="button"
            onclick={toggleMenu}
            onkeydown={onTriggerKeydown}
            class="menu-trigger"
            aria-label={$_("menu")}
            title={$_("menu")}
            aria-expanded={isMenuOpen}
            aria-controls={isMenuOpen ? "dashboard-main-menu" : undefined}
            aria-haspopup="menu"
          >
            <BarsOutline size="md" aria-hidden="true" />
            <span class="avatar-chip" aria-hidden="true">{getInitials($user)}</span>
          </button>

          {#if isMenuOpen}
            <div class="menu-backdrop" aria-hidden="true"></div>
            <div
              id="dashboard-main-menu"
              role="menu"
              aria-label={$_("ui.main_menu")}
              tabindex="-1"
              class="dropdown-menu"
              bind:this={menuEl}
              onkeydown={onMenuKeydown}
            >
              <div class="menu-mobile-head">
                <span class="menu-user">
                  <span class="avatar-chip" aria-hidden="true">{getInitials($user)}</span>
                  <span class="truncate">{$user.localized_displayname || $user.shortname}</span>
                </span>
                <IconButton label={$_("ui.close_menu")} size="sm" onclick={() => closeMenu(true)}>
                  <CloseOutline size="sm" />
                </IconButton>
              </div>

              <div class="dropdown-scroll">
                {#if hasAdminAccess}
                  <div class="menu-section" role="group" aria-label={$_("admin")}>
                    <div class="menu-section-title" aria-hidden="true">{$_("admin")}</div>
                    {#each adminEntries as entry (entry.href)}
                      {@render menuLink(entry)}
                    {/each}
                  </div>
                  <div class="menu-divider" role="separator"></div>
                {/if}

                <div class="menu-section">
                  {#each mainEntries as entry (entry.href)}
                    {@render menuLink(entry)}
                  {/each}
                </div>
              </div>

              <div class="dropdown-footer">
                <button type="button" role="menuitem" onclick={handleLogout} class="menu-item logout-item">
                  <svg class="menu-icon" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1"
                    />
                  </svg>
                  <span>{$_("sign_out")}</span>
                </button>
              </div>
            </div>
          {/if}
        </div>
      {:else}
        <a href={withBase("/login")} class="login-btn">{$_("Login")}</a>
      {/if}
    </div>
  </div>
</header>

<style>
  /* No backdrop-filter here: a filter makes the header the containing block
     for position:fixed descendants, which would pin the phone drawer inside
     the 3.5rem bar instead of the viewport. */
  .site-header {
    position: sticky;
    top: 0;
    z-index: 40;
    width: 100%;
    background: var(--color-surface-2);
    border-bottom: 1px solid var(--color-border);
    box-shadow: var(--shadow-xs);
  }

  .site-header-bar {
    max-width: 1500px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
    height: 3.5rem;
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .brand {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    text-decoration: none;
    flex-shrink: 0;
    border-radius: var(--radius-control);
  }

  .brand-name {
    font-weight: 700;
    font-size: 1.0625rem;
    letter-spacing: -0.02em;
    color: var(--color-text);
    transition: color var(--duration-fast) ease;
  }

  .brand:hover .brand-name {
    color: var(--color-primary);
  }

  .site-search {
    flex: 1;
    min-width: 0;
  }

  .controls {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    margin-inline-start: auto;
    flex-shrink: 0;
  }

  .menu-container {
    position: relative;
    display: flex;
    align-items: center;
  }

  .menu-trigger {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    height: 2.25rem;
    padding: 0 0.375rem 0 0.5rem;
    border-radius: var(--radius-full);
    border: 1px solid var(--color-border);
    background: var(--color-surface-2);
    color: var(--color-text-muted);
    cursor: pointer;
    transition: background var(--duration-fast) ease, border-color var(--duration-fast) ease;
  }

  .menu-trigger:hover,
  .menu-trigger[aria-expanded="true"] {
    background: var(--color-surface-3);
    border-color: var(--color-border-strong);
    color: var(--color-text);
  }

  .avatar-chip {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 1.625rem;
    height: 1.625rem;
    border-radius: var(--radius-full);
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    font-weight: 600;
    font-size: 0.625rem;
    letter-spacing: 0.02em;
    flex-shrink: 0;
  }

  .dropdown-menu {
    position: absolute;
    top: calc(100% + 0.625rem);
    inset-inline-end: 0;
    z-index: 50;
    min-width: 15rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-modal);
    animation: dropdown-enter 0.2s var(--ease-out);
    overflow: hidden;
    overscroll-behavior: contain;
    display: flex;
    flex-direction: column;
    outline: none;
  }

  @keyframes dropdown-enter {
    from {
      opacity: 0;
      transform: translateY(-6px) scale(0.97);
    }
    to {
      opacity: 1;
      transform: translateY(0) scale(1);
    }
  }

  .menu-backdrop {
    display: none;
  }

  .menu-mobile-head {
    display: none;
  }

  .dropdown-scroll {
    padding: 0.5rem;
    max-height: 60vh;
    overflow-y: auto;
  }

  .dropdown-footer {
    padding: 0.5rem;
    border-top: 1px solid var(--color-border);
  }

  .menu-section {
    margin-bottom: 0.25rem;
  }

  .menu-section:last-child {
    margin-bottom: 0;
  }

  .menu-section-title {
    font-size: 0.6875rem;
    font-weight: 600;
    color: var(--color-text-faint);
    text-transform: uppercase;
    letter-spacing: 0.06em;
    padding: 0.5rem 0.75rem 0.25rem;
  }

  .menu-item {
    display: flex;
    align-items: center;
    width: 100%;
    padding: 0.5rem 0.75rem;
    border: none;
    background: none;
    border-radius: var(--radius-control);
    cursor: pointer;
    transition:
      background var(--duration-fast) ease,
      color var(--duration-fast) ease;
    text-align: start;
    text-decoration: none;
    color: var(--color-text);
    font-size: 0.8125rem;
    font-weight: 500;
    position: relative;
    gap: 0.625rem;
  }

  .menu-item:hover,
  .menu-item:focus-visible {
    background: var(--color-surface-3);
    color: var(--color-text);
    outline: none;
  }

  .menu-item:focus-visible {
    box-shadow: inset 0 0 0 2px var(--color-primary);
  }

  .menu-icon {
    width: 1.125rem;
    height: 1.125rem;
    flex-shrink: 0;
    color: var(--color-text-faint);
    transition: color var(--duration-fast) ease;
  }

  .menu-item:hover .menu-icon,
  .menu-item:focus-visible .menu-icon {
    color: var(--color-primary);
  }

  .menu-item span {
    flex: 1;
  }

  .logout-item:hover,
  .logout-item:focus-visible {
    background: var(--color-danger-soft);
    color: var(--color-danger);
  }

  .logout-item:hover .menu-icon,
  .logout-item:focus-visible .menu-icon {
    color: var(--color-danger);
  }

  .menu-divider {
    height: 1px;
    background: var(--color-border);
    margin: 0.375rem 0.5rem;
  }

  .notification-badge {
    width: 0.4375rem;
    height: 0.4375rem;
    background: var(--color-danger);
    border-radius: var(--radius-full);
    margin-inline-start: auto;
    flex: none !important;
    animation: pulse-soft 2s ease-in-out infinite;
  }

  .login-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    padding: 0.5rem 1.125rem;
    margin-inline-start: 0.25rem;
    background: var(--color-primary);
    color: var(--color-text-on-primary);
    font-weight: 600;
    font-size: 0.8125rem;
    border: none;
    border-radius: var(--radius-control);
    cursor: pointer;
    text-decoration: none;
    transition: background var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-xs);
  }

  .login-btn:hover {
    background: var(--color-primary-hover);
  }

  /* Phone: the menu becomes a drawer from the inline-end edge. */
  @media (max-width: 640px) {
    .site-header-bar {
      gap: 0.5rem;
    }

    .brand-name {
      display: none;
    }

    .menu-backdrop {
      display: block;
      position: fixed;
      inset: 0;
      z-index: 49;
      background: var(--surface-overlay);
      animation: fadeIn var(--duration-fast) var(--ease-out);
    }

    .dropdown-menu {
      position: fixed;
      top: 0;
      bottom: 0;
      inset-inline-end: 0;
      width: min(20rem, 85vw);
      min-width: 0;
      max-height: none;
      border-radius: 0;
      border-width: 0;
      border-inline-start-width: 1px;
      animation: drawer-enter 0.25s var(--ease-out);
    }

    @keyframes drawer-enter {
      from {
        transform: translateX(var(--drawer-offset, 100%));
      }
      to {
        transform: translateX(0);
      }
    }

    :global([dir="rtl"]) .dropdown-menu {
      --drawer-offset: -100%;
    }

    .menu-mobile-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
      padding: 0.75rem 0.75rem 0.75rem 1rem;
      border-bottom: 1px solid var(--color-border);
    }

    .menu-user {
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
      min-width: 0;
      font-size: 0.875rem;
      font-weight: 600;
      color: var(--color-text);
    }

    .dropdown-scroll {
      flex: 1;
      max-height: none;
    }

    .login-btn {
      padding: 0.5rem 0.875rem;
      font-size: 0.75rem;
    }
  }
</style>
