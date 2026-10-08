<script lang="ts">
    import type { Snippet } from "svelte";

    // An icon-only control always has a name: `label` is required and becomes
    // both aria-label and the hover title.
    let {
        label,
        href,
        onclick,
        type = "button",
        size = "md",
        variant = "ghost",
        disabled = false,
        pressed,
        expanded,
        controls,
        class: className = "",
        children,
        ...rest
    }: {
        label: string;
        href?: string;
        onclick?: (event: MouseEvent) => void;
        type?: "button" | "submit";
        size?: "sm" | "md";
        variant?: "ghost" | "outline" | "danger";
        disabled?: boolean;
        /** aria-pressed, for toggles. */
        pressed?: boolean;
        /** aria-expanded, for menu/disclosure triggers. */
        expanded?: boolean;
        /** aria-controls: id of the element this button toggles. */
        controls?: string;
        class?: string;
        children: Snippet;
        [key: string]: unknown;
    } = $props();

    const tones: Record<string, string> = {
        ghost: "text-text-muted hover:text-text hover:bg-surface-3",
        outline: "text-text-muted border border-border bg-surface-2 hover:text-text hover:bg-surface-3",
        danger: "text-danger hover:bg-danger-soft",
    };
    const base = $derived(
        `inline-flex items-center justify-center rounded-control transition-colors cursor-pointer disabled:opacity-50 disabled:cursor-not-allowed ${size === "sm" ? "w-7 h-7" : "w-9 h-9"} ${tones[variant]} ${className}`,
    );
</script>

{#if href}
    <a {href} class={base} aria-label={label} title={label} {...rest}>
        {@render children()}
    </a>
{:else}
    <button
        {type}
        class={base}
        aria-label={label}
        title={label}
        aria-pressed={pressed}
        aria-expanded={expanded}
        aria-controls={controls}
        {disabled}
        {onclick}
        {...rest}
    >
        {@render children()}
    </button>
{/if}
