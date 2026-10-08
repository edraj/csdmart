<script lang="ts">
    import { Button, Modal, Spinner } from "flowbite-svelte";
    import { ExclamationCircleOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";

    // "Saving this will affect N other things — go ahead?" The one dialog
    // behind the schema, permission and role impact warnings in EntryRenderer.
    let {
        open = $bindable(false),
        title,
        message,
        details = [],
        question,
        subject,
        confirmLabel,
        loading = false,
        onConfirm,
        onCancel,
    }: {
        open?: boolean;
        title: string;
        /** The warning line, already formatted ("3 roles will be affected ..."). */
        message: string;
        /** Optional list under the warning (e.g. the affected role names). */
        details?: string[];
        /** The question, without the subject ("Are you sure you want to update the role"). */
        question: string;
        /** Rendered bold after the question. */
        subject: string;
        confirmLabel?: string;
        loading?: boolean;
        onConfirm: () => void;
        onCancel: () => void;
    } = $props();
</script>

<Modal bind:open size="md" {title} permanent={loading} focustrap class="rounded-modal shadow-modal">
    <div class="space-y-4">
        <div
            class="flex items-start gap-3 rounded-card border border-warning/30 bg-warning-soft p-4 text-start"
            role="alert"
        >
            <ExclamationCircleOutline class="shrink-0 mt-0.5 text-warning" size="md" aria-hidden="true" />
            <div class="min-w-0">
                <p class="text-sm font-medium text-text">{message}</p>
                {#if details.length > 0}
                    <ul class="mt-2 list-disc list-inside text-sm text-text-muted">
                        {#each details as item (item)}
                            <li>{item}</li>
                        {/each}
                    </ul>
                {/if}
            </div>
        </div>
        <p class="text-sm text-text">
            {question}
            <span class="font-semibold">{subject}</span>?
        </p>
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={onCancel} disabled={loading}>{$_("cancel")}</Button>
        <Button color="primary" onclick={onConfirm} disabled={loading} data-autofocus>
            {#if loading}
                <Spinner class="me-2" size="4" />
                {$_("saving")}
            {:else}
                {confirmLabel ?? $_("confirm_update")}
            {/if}
        </Button>
    </div>
</Modal>
