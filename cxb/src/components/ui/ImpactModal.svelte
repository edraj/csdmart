<script lang="ts">
    import { Button, Modal } from "flowbite-svelte";
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

<Modal bind:open size="md" {title}>
    <div class="text-center mb-6">
        <div
            class="bg-yellow-50 border-s-4 border-yellow-400 p-4 mb-4 text-start dark:bg-yellow-900/20 dark:border-yellow-500"
            role="alert"
        >
            <p class="text-sm text-yellow-700 font-medium dark:text-yellow-400">
                <span aria-hidden="true">&#9888;</span>
                {message}
            </p>
            {#if details.length > 0}
                <ul class="mt-2 list-disc list-inside text-sm text-yellow-700 dark:text-yellow-400">
                    {#each details as item (item)}
                        <li>{item}</li>
                    {/each}
                </ul>
            {/if}
        </div>
        <p>
            {question}
            <span class="font-bold">{subject}</span>?
        </p>
    </div>

    <div class="flex justify-between w-full">
        <Button class="cursor-pointer" color="alternative" onclick={onCancel}>{$_("cancel")}</Button>
        <Button class="bg-primary cursor-pointer" onclick={onConfirm} disabled={loading}>
            {loading ? $_("saving") : confirmLabel ?? $_("confirm_update")}
        </Button>
    </div>
</Modal>
