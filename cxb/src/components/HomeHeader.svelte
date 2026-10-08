<script lang="ts">
    import { Avatar, Dropdown, DropdownDivider, DropdownItem } from "flowbite-svelte";
    import { CheckOutline, HomeSolid, LanguageOutline, OpenDoorOutline, ToggleHeaderRowOutline, UserSolid } from "flowbite-svelte-icons";
    import { goto, url } from "@roxi/routify";
    import { signout, user } from "@/stores/user";
    import { getAvatar } from "@/lib/dmart_services";
    import { _, enabledLocales, locale, switchLocale } from "@/i18n";
    import { website } from "@/config";

    // Header for the public pages (landing, password reset). The management
    // area has its own header with the same controls.
    const languages = enabledLocales();

    let avatarUrl: string | null = $state(null);
    let loadedShortname = "";
    $effect(() => {
        const shortname = $user?.shortname ?? "";
        if (shortname && shortname !== loadedShortname) {
            loadedShortname = shortname;
            getAvatar(shortname)
                .then((u) => { avatarUrl = u; })
                .catch(() => { avatarUrl = null; });
        }
    });

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
</script>

<header class="flex items-center justify-between gap-4 border-b border-border bg-surface-2 px-4 sm:px-6 h-14">
    <!-- Routed through $url so the link stays inside the /cxb/ base. -->
    <a href={$url("/")} class="flex items-center gap-2 text-text font-semibold rounded-control">
        <HomeSolid size="lg" class="text-primary" aria-hidden="true" />
        <span>{website.display_name || "dmart"}</span>
    </a>

    <div class="flex items-center gap-2 sm:gap-3">
        {#if languages.length > 1}
            <button
                type="button"
                id="home-language-menu"
                class="inline-flex items-center gap-1.5 h-9 px-3 rounded-control text-sm text-text-muted hover:text-text hover:bg-surface-3 transition-colors cursor-pointer"
                aria-label={$_("language")}
                aria-haspopup="menu"
            >
                <LanguageOutline size="sm" aria-hidden="true" />
                <span class="uppercase">{$locale}</span>
            </button>
            <Dropdown simple triggeredBy="#home-language-menu" class="min-w-40">
                {#each languages as code (code)}
                    <DropdownItem onclick={() => switchLocale(code)} aria-current={$locale === code ? "true" : undefined}>
                        <span class="flex items-center justify-between gap-3" lang={code}>
                            <span>{website.languages[code]}</span>
                            {#if $locale === code}<CheckOutline size="sm" class="text-primary" aria-hidden="true" />{/if}
                        </span>
                    </DropdownItem>
                {/each}
            </Dropdown>
        {/if}

        {#if !$user || !$user.signedin}
            <a
                href={$url("/management")}
                class="inline-flex items-center h-9 px-4 rounded-control bg-primary text-text-on-primary text-sm font-medium hover:bg-primary-hover transition-colors"
            >
                {$_("login")}
            </a>
        {:else}
            <button
                type="button"
                id="home-user-menu"
                class="inline-flex items-center gap-2 h-9 ps-1 pe-3 rounded-full border border-border bg-surface-2 text-sm text-text hover:bg-surface-3 transition-colors cursor-pointer"
                aria-haspopup="menu"
                aria-label={$_("account_menu")}
            >
                <Avatar src={avatarUrl ?? undefined} size="xs" />
                <span>{$user.shortname}</span>
            </button>
            <Dropdown simple triggeredBy="#home-user-menu" class="min-w-44">
                <DropdownItem href={$url("/management")}>
                    <span class="flex items-center gap-2">
                        <ToggleHeaderRowOutline size="sm" aria-hidden="true" /> {$_("dashboard")}
                    </span>
                </DropdownItem>
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
        {/if}
    </div>
</header>
