<script lang="ts">
    import { Avatar, CloseButton, Drawer, Dropdown, DropdownDivider, DropdownItem } from "flowbite-svelte";
    import {
        BarsOutline,
        CheckOutline,
        FolderSolid,
        LanguageOutline,
        MoonOutline,
        OpenDoorOutline,
        SunOutline,
        UserSettingsSolid,
        UserSolid,
    } from "flowbite-svelte-icons";
    import { activeRoute, goto, url } from "@roxi/routify";
    import { signout, user } from "@/stores/user";
    import { getAvatar } from "@/lib/dmart_services";
    import { _, dir, enabledLocales, locale, switchLocale } from "@/i18n";
    import { website } from "@/config";
    import { theme } from "@/stores/theme.svelte";
    import { navbarTheme, isDarkBackground } from "@/stores/navbar_theme";
    import IconButton from "@/components/ui/IconButton.svelte";

    let customBg = $derived($navbarTheme?.value);
    let onCustomDark = $derived(isDarkBackground($navbarTheme));

    type Tab = { href: string; match: string; labelKey: string; icon: typeof FolderSolid };
    const tabs: Tab[] = [
        { href: "/management/content", match: "/management/content", labelKey: "spaces", icon: FolderSolid },
        { href: "/management/tools", match: "/management/tools", labelKey: "tools", icon: UserSettingsSolid },
    ];

    // Built from config.json: only languages the operator enabled and this
    // build ships messages for.
    const languages = enabledLocales();

    let avatarUrl: string | null = $state(null);
    let loadedShortname = "";
    $effect(() => {
        const shortname = $user.shortname ?? "";
        if (shortname && shortname !== loadedShortname) {
            loadedShortname = shortname;
            getAvatar(shortname)
                .then((u) => { avatarUrl = u; })
                .catch(() => { avatarUrl = null; });
        }
    });

    let drawerOpen = $state(false);

    function goToProfile(e: Event) {
        e.preventDefault();
        e.stopPropagation();
        $goto("/management/profile");
    }

    function logout(e: Event) {
        e.preventDefault();
        e.stopPropagation();
        signout();
    }

    function navigate(href: string) {
        drawerOpen = false;
        $goto(href);
    }

    function isActive(match: string) {
        return $activeRoute.url.includes(match);
    }

    // The menu must close itself after a choice; flowbite's simple Dropdown
    // only toggles on its trigger.
    let languageMenuOpen = $state(false);
</script>

<header
    class="flex items-center justify-between gap-3 border-b px-4 sm:px-6 h-14 transition-colors"
    class:border-border={!customBg}
    class:border-transparent={!!customBg}
    class:bg-surface-2={!customBg}
    class:on-custom-dark={onCustomDark}
    style:background={customBg}
>
    <!-- Desktop tabs -->
    <nav class="hidden md:block me-auto h-full" aria-label={$_("primary_navigation")}>
        <ul class="flex flex-row gap-8 h-full">
            {#each tabs as tab (tab.href)}
                {@const Icon = tab.icon}
                {@const current = isActive(tab.match)}
                <li class="relative flex items-center">
                    <a
                        href={$url(tab.href)}
                        onclick={(e) => { e.preventDefault(); navigate(tab.href); }}
                        aria-current={current ? "page" : undefined}
                        class="flex items-center gap-2 text-sm font-medium text-text-muted aria-[current=page]:text-primary hover:text-primary transition-colors rounded-control"
                    >
                        <Icon size="md" aria-hidden="true" />
                        <span>{$_(tab.labelKey)}</span>
                    </a>
                    {#if current}
                        <div class="absolute bottom-0 start-0 end-0 h-0.5 bg-primary rounded-t" aria-hidden="true"></div>
                    {/if}
                </li>
            {/each}
        </ul>
    </nav>

    <!-- Mobile hamburger -->
    <div class="md:hidden me-auto">
        <IconButton
            label={$_("open_navigation_menu")}
            expanded={drawerOpen}
            controls="management-nav-drawer"
            onclick={() => (drawerOpen = true)}
        >
            <BarsOutline size="md" />
        </IconButton>
    </div>

    <div class="flex items-center gap-1 sm:gap-2">
        <!-- Language menu -->
        {#if languages.length > 1}
            <button
                type="button"
                id="management-language-menu"
                class="inline-flex items-center gap-1.5 h-9 px-2.5 rounded-control text-sm text-text-muted hover:text-text hover:bg-surface-3 transition-colors cursor-pointer"
                aria-label={$_("language")}
                aria-haspopup="menu"
            >
                <LanguageOutline size="sm" aria-hidden="true" />
                <span class="uppercase">{$locale}</span>
            </button>
            <Dropdown simple triggeredBy="#management-language-menu" class="min-w-40" bind:isOpen={languageMenuOpen}>
                {#each languages as code (code)}
                    <DropdownItem onclick={() => { switchLocale(code); languageMenuOpen = false; }} aria-current={$locale === code ? "true" : undefined}>
                        <span class="flex items-center justify-between gap-3" lang={code}>
                            <span>{website.languages[code]}</span>
                            {#if $locale === code}<CheckOutline size="sm" class="text-primary" aria-hidden="true" />{/if}
                        </span>
                    </DropdownItem>
                {/each}
            </Dropdown>
        {/if}

        <!-- Dark-mode toggle -->
        <IconButton
            label={theme.resolved === "dark" ? $_("switch_to_light_mode") : $_("switch_to_dark_mode")}
            pressed={theme.resolved === "dark"}
            onclick={() => theme.toggle()}
        >
            {#if theme.resolved === "dark"}
                <SunOutline size="sm" />
            {:else}
                <MoonOutline size="sm" />
            {/if}
        </IconButton>

        <!-- Account menu: a plain button; the Dropdown is its sibling -->
        <button
            type="button"
            id="management-user-menu"
            class="inline-flex items-center gap-2 h-9 ps-1 pe-3 rounded-full border border-border bg-surface-2 text-sm text-text hover:bg-surface-3 transition-colors cursor-pointer"
            aria-haspopup="menu"
            aria-label={$_("account_menu")}
        >
            <Avatar src={avatarUrl ?? undefined} size="xs" />
            <span class="max-w-32 truncate">{$user.shortname}</span>
        </button>
        <Dropdown simple triggeredBy="#management-user-menu" class="min-w-44">
            <DropdownItem onclick={goToProfile}>
                <span class="flex items-center gap-2">
                    <UserSolid size="sm" aria-hidden="true" /> {$_("profile")}
                </span>
            </DropdownItem>
            <DropdownDivider />
            <DropdownItem onclick={logout}>
                <span class="flex items-center gap-2 text-danger">
                    <OpenDoorOutline size="sm" aria-hidden="true" /> {$_("logout")}
                </span>
            </DropdownItem>
        </Dropdown>
    </div>
</header>

<!-- Mobile navigation drawer -->
<Drawer
    placement={$dir === "rtl" ? "right" : "left"}
    bind:open={drawerOpen}
    id="management-nav-drawer"
    class="bg-surface-2 text-text"
>
    <div class="flex items-center justify-between mb-4">
        <h2 class="text-base font-semibold text-text">{$_("menu")}</h2>
        <CloseButton onclick={() => (drawerOpen = false)} aria-label={$_("close")} />
    </div>
    <nav aria-label={$_("primary_navigation")}>
        <ul class="flex flex-col gap-1">
            {#each tabs as tab (tab.href)}
                {@const Icon = tab.icon}
                {@const current = isActive(tab.match)}
                <li>
                    <a
                        href={$url(tab.href)}
                        onclick={(e) => { e.preventDefault(); navigate(tab.href); }}
                        aria-current={current ? "page" : undefined}
                        class="flex items-center gap-3 rounded-control px-3 py-2 text-text hover:bg-surface-3
                            aria-[current=page]:bg-primary-soft aria-[current=page]:text-primary transition-colors"
                    >
                        <Icon size="md" aria-hidden="true" />
                        <span>{$_(tab.labelKey)}</span>
                    </a>
                </li>
            {/each}
        </ul>
    </nav>
</Drawer>

<style>
    /* A custom (operator-chosen) header background is always dark; the token
       colours do not know about it, so force readable foregrounds here. */
    :global(.on-custom-dark a),
    :global(.on-custom-dark button) {
        color: #ffffff;
    }
    :global(.on-custom-dark a[aria-current="page"]) {
        color: #ffffff;
    }
    :global(.on-custom-dark a[aria-current="page"] + div) {
        background: #ffffff;
    }
    :global(.on-custom-dark button:hover) {
        background: rgba(255, 255, 255, 0.18);
    }
    :global(.on-custom-dark #management-user-menu) {
        background: rgba(255, 255, 255, 0.15);
        border-color: transparent;
    }
</style>
