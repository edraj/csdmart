<script lang="ts">
    import type { Snippet } from "svelte";
    import { Spinner } from "flowbite-svelte";
    import { _ } from "@/i18n";

    // Three shapes, one component:
    //   spinner  — centred spinner + label, for a page that has nothing yet
    //   skeleton — N text-shaped bars, full width of the container (never 100vw)
    //   overlay  — wraps existing content and dims it with a spinner on top,
    //              so a refresh never blanks a table that is already there
    let {
        variant = "spinner",
        loading = true,
        label,
        rows = 5,
        class: className = "",
        children,
    }: {
        variant?: "spinner" | "skeleton" | "overlay";
        /** Only meaningful for "overlay": false renders the children alone. */
        loading?: boolean;
        label?: string;
        rows?: number;
        class?: string;
        children?: Snippet;
    } = $props();

    const text = $derived(label ?? $_("loading"));
</script>

{#if variant === "overlay"}
    <div class="relative {className}">
        {@render children?.()}
        {#if loading}
            <div
                class="absolute inset-0 z-10 flex items-center justify-center bg-surface-2/60 backdrop-blur-[1px] rounded-card"
                role="status"
                aria-live="polite"
            >
                <Spinner size="8" />
                <span class="sr-only">{text}</span>
            </div>
        {/if}
    </div>
{:else if variant === "skeleton"}
    <div class="w-full space-y-3 animate-pulse {className}" role="status" aria-live="polite" aria-busy="true">
        {#each Array.from({ length: Math.max(1, rows) }, (_, i) => i) as i (i)}
            <div class="flex items-center gap-3">
                <div class="h-3 rounded-full bg-surface-3" style="width: {i % 3 === 0 ? 55 : i % 3 === 1 ? 85 : 70}%"></div>
                <div class="h-3 w-16 rounded-full bg-surface-3 ms-auto"></div>
            </div>
        {/each}
        <span class="sr-only">{text}</span>
    </div>
{:else}
    <div class="flex flex-col items-center justify-center gap-3 py-12 text-text-muted {className}" role="status" aria-live="polite">
        <Spinner size="8" />
        <span class="text-sm">{text}</span>
    </div>
{/if}
