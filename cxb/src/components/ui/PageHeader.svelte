<script lang="ts">
    import type { Component, Snippet } from "svelte";
    import { url } from "@roxi/routify";
    import { ArrowLeftOutline } from "flowbite-svelte-icons";

    // The one page title block: optional breadcrumb or back link above, title
    // (24/600) with an optional description (14, muted) at the start, actions
    // at the end. Wraps on narrow widths.
    let {
        title,
        description,
        icon,
        iconTone = "primary",
        backHref,
        backLabel,
        breadcrumb,
        actions,
        class: className = "",
    }: {
        title: string;
        description?: string;
        /** Optional leading icon component (flowbite-svelte-icons). */
        icon?: Component<Record<string, unknown>>;
        iconTone?: "primary" | "danger";
        /** Internal route path for a "back" link rendered above the title. */
        backHref?: string;
        backLabel?: string;
        /** Rendered above the title instead of / in addition to the back link. */
        breadcrumb?: Snippet;
        actions?: Snippet;
        class?: string;
    } = $props();

    const Icon = $derived(icon);
</script>

<header class="mb-6 {className}">
    {#if breadcrumb}
        <div class="mb-3">{@render breadcrumb()}</div>
    {/if}
    {#if backHref && backLabel}
        <a
            href={$url(backHref)}
            class="inline-flex items-center gap-1.5 text-sm text-text-muted hover:text-primary mb-3 transition-colors rounded-control"
        >
            <ArrowLeftOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
            <span>{backLabel}</span>
        </a>
    {/if}
    <div class="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
        <div class="flex items-start gap-3 min-w-0">
            {#if Icon}
                <div
                    class="shrink-0 w-11 h-11 rounded-full flex items-center justify-center
                        {iconTone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-primary-soft text-primary'}"
                    aria-hidden="true"
                >
                    <Icon size="lg" />
                </div>
            {/if}
            <div class="min-w-0">
                <h1 class="text-2xl font-semibold text-text leading-tight break-words">{title}</h1>
                {#if description}
                    <p class="mt-1 text-sm text-text-muted">{description}</p>
                {/if}
            </div>
        </div>
        {#if actions}
            <div class="flex flex-wrap items-center gap-2 ms-auto">
                {@render actions()}
            </div>
        {/if}
    </div>
</header>
