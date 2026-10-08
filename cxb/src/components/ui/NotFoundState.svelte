<script lang="ts">
    import { Button } from "flowbite-svelte";
    import { QuestionCircleOutline } from "flowbite-svelte-icons";
    import { url } from "@roxi/routify";
    import EmptyState from "./EmptyState.svelte";
    import { _ } from "@/i18n";

    // Shown where a route cannot resolve what it was asked for: a URL without
    // the parameters it needs, or an address that matches no route at all.
    let {
        title,
        hint,
        href = "/management/content",
    }: {
        title?: string;
        hint?: string;
        /** Internal route the action button leads to; null hides the button. */
        href?: string | null;
    } = $props();
</script>

<div class="p-4 sm:p-6">
    <EmptyState
        icon={QuestionCircleOutline}
        title={title ?? $_("page_not_found")}
        hint={hint ?? $_("page_not_found_hint")}
    >
        {#if href}
            <Button href={$url(href)} color="alternative" size="sm">{$_("go_to_spaces")}</Button>
        {/if}
    </EmptyState>
</div>
