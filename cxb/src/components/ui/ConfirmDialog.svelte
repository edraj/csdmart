<script lang="ts">
    import type { Snippet } from "svelte";
    import { Button, Modal, Spinner } from "flowbite-svelte";
    import { _ } from "@/i18n";
    import ErrorState from "./ErrorState.svelte";

    // Every "are you sure?" in the app. Escape and the overlay close it (the
    // flowbite Modal is a native <dialog>, which also traps focus); while
    // `loading` it cannot be dismissed, so a half-finished delete is never
    // left without feedback.
    let {
        open = $bindable(false),
        title,
        body,
        variant = "primary",
        confirmLabel,
        cancelLabel,
        loading = false,
        loadingLabel,
        error,
        onConfirm,
        onCancel,
        children,
    }: {
        open?: boolean;
        title: string;
        /** Plain-text body; use `children` for rich content. */
        body?: string;
        variant?: "danger" | "primary";
        confirmLabel?: string;
        cancelLabel?: string;
        loading?: boolean;
        loadingLabel?: string;
        /** Shown inside the dialog so a failed action is explained in place. */
        error?: unknown;
        onConfirm: () => void | Promise<void>;
        onCancel?: () => void;
        children?: Snippet;
    } = $props();

    function cancel() {
        if (loading) return;
        open = false;
        onCancel?.();
    }

    const confirmText = $derived(confirmLabel ?? (variant === "danger" ? $_("delete") : $_("confirm")));
</script>

<Modal
    bind:open
    size="sm"
    {title}
    permanent={loading}
    focustrap
    oncancel={() => {
        if (!loading) onCancel?.();
    }}
    class="rounded-modal shadow-modal"
>
    <div class="space-y-4">
        {#if body}
            <p class="text-sm text-text-muted whitespace-pre-line">{body}</p>
        {/if}
        {@render children?.()}
        {#if error}
            <ErrorState compact {error} />
        {/if}
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={cancel} disabled={loading}>
            {cancelLabel ?? $_("cancel")}
        </Button>
        <Button
            color={variant === "danger" ? "red" : "primary"}
            onclick={() => void onConfirm()}
            disabled={loading}
            data-autofocus
        >
            {#if loading}
                <Spinner class="me-2" size="4" color={variant === "danger" ? "red" : "primary"} />
                {loadingLabel ?? $_("loading")}
            {:else}
                {confirmText}
            {/if}
        </Button>
    </div>
</Modal>
