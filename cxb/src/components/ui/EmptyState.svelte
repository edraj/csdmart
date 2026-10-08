<script lang="ts">
    import { InboxOutline } from "flowbite-svelte-icons";
    import type { Component, Snippet } from "svelte";
    import { _ } from "@/i18n";

    let {
        icon,
        title,
        hint,
        children,
        class: className = "",
    }: {
        icon?: Component<Record<string, unknown>>;
        title?: string;
        hint?: string;
        /** Optional action (a button or link). */
        children?: Snippet;
        class?: string;
    } = $props();

    const Icon = $derived(icon ?? InboxOutline);
</script>

<div
    class="flex flex-col items-center justify-center text-center py-12 px-6 rounded-card border border-dashed border-border bg-surface-2 {className}"
>
    <div
        class="w-14 h-14 mb-3 rounded-full flex items-center justify-center bg-primary-soft text-primary"
        aria-hidden="true"
    >
        <Icon size="lg" />
    </div>
    <h3 class="text-base font-semibold text-text">{title ?? $_("no_records_found")}</h3>
    {#if hint}
        <p class="mt-1 text-sm text-text-muted max-w-md">{hint}</p>
    {/if}
    {#if children}
        <div class="mt-4">{@render children()}</div>
    {/if}
</div>
