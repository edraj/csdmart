<script lang="ts">
    import type { Snippet } from "svelte";

    let {
        variant = "neutral",
        size = "md",
        class: className = "",
        children,
        ...rest
    }: {
        variant?: "neutral" | "primary" | "success" | "danger" | "warning" | "info";
        size?: "sm" | "md";
        class?: string;
        children: Snippet;
        [key: string]: unknown;
    } = $props();

    const tones: Record<string, string> = {
        neutral: "bg-surface-3 text-text-muted",
        primary: "bg-primary-soft text-primary",
        success: "bg-success-soft text-success",
        danger: "bg-danger-soft text-danger",
        warning: "bg-warning-soft text-warning",
        info: "bg-info-soft text-info",
    };
</script>

<span
    class="inline-flex items-center gap-1 rounded-full font-medium whitespace-nowrap tabular-nums
        {size === 'sm' ? 'px-2 py-0.5 text-xs' : 'px-2.5 py-0.5 text-sm'} {tones[variant]} {className}"
    {...rest}
>
    {@render children()}
</span>
