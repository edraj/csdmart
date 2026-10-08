<script lang="ts">
    import type { Snippet } from "svelte";
    import { CloseButton, Drawer } from "flowbite-svelte";
    import { BarsOutline } from "flowbite-svelte-icons";
    import { params } from "@roxi/routify";
    import SpaceSubpathItemsSidebar from "@/components/management/SpaceSubpathItemsSidebar.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { _, dir } from "@/i18n";

    let { children }: { children?: Snippet } = $props();

    let drawerOpen = $state(false);
</script>

<div class="flex h-full relative">
    <!-- Desktop sidebar -->
    <aside class="hidden md:block w-64 border-e border-border bg-surface-2 h-full shrink-0 overflow-y-auto">
        <SpaceSubpathItemsSidebar />
    </aside>

    <!-- Phone: the same tree in a drawer -->
    <Drawer
        placement={$dir === "rtl" ? "right" : "left"}
        bind:open={drawerOpen}
        id="content-sidebar-drawer"
        class="md:hidden w-72 bg-surface-2 text-text p-0"
    >
        <div class="flex flex-col h-full">
            <div class="flex items-center justify-between px-4 h-14 border-b border-border shrink-0">
                <h2 class="text-sm font-semibold text-text-muted truncate">{$_("folders")}</h2>
                <CloseButton onclick={() => (drawerOpen = false)} aria-label={$_("close")} />
            </div>
            <div class="flex-1 overflow-y-auto">
                <SpaceSubpathItemsSidebar onNavigate={() => (drawerOpen = false)} />
            </div>
        </div>
    </Drawer>

    <!-- Main content -->
    <div class="flex-1 overflow-auto flex flex-col h-full w-full min-w-0">
        <div class="md:hidden flex items-center gap-3 px-4 h-12 border-b border-border bg-surface-2 sticky top-0 z-10">
            <IconButton
                label={$_("open_folder_tree")}
                variant="outline"
                expanded={drawerOpen}
                controls="content-sidebar-drawer"
                onclick={() => (drawerOpen = true)}
            >
                <BarsOutline size="sm" />
            </IconButton>
            <span class="font-semibold text-sm truncate">{$params.space_name}</span>
        </div>

        <div class="flex-1 overflow-auto w-full pb-8">
            {@render children?.()}
        </div>
    </div>
</div>
