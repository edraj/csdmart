<script lang="ts">
  import type { Snippet } from "svelte";
  import { ExclamationCircleOutline, QuestionCircleOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import { _ } from "@/i18n";
  import ErrorState from "./ErrorState.svelte";

  // Every "are you sure?" in the app, built on components/Modal.svelte (so
  // Escape and the overlay close it and focus is trapped). Two ways to use it:
  //
  //   1. Declaratively — the page owns `open` and `loading`:
  //        <ConfirmDialog bind:open title=… variant="danger" onConfirm={doDelete} />
  //
  //   2. With `action` — the dialog runs it, shows the loading state, stays
  //      open with the error if it throws, and closes itself on success:
  //        <ConfirmDialog bind:open title=… action={() => deleteEntry(id)} />
  //
  // The promise-returning `confirm()` in lib/confirm.ts wraps this for the
  // `if (await confirm({...}))` call sites.
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
    action,
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
    /** Page-controlled busy state (when not using `action`). */
    loading?: boolean;
    loadingLabel?: string;
    /** Shown inside the dialog so a failed action is explained in place. */
    error?: unknown;
    /** Runs on confirm; failure keeps the dialog open with the error. */
    action?: () => Promise<unknown> | unknown;
    /** Called after confirm (after `action` succeeded, when one is given). */
    onConfirm?: () => void | Promise<void>;
    onCancel?: () => void;
    children?: Snippet;
  } = $props();

  let busy = $state(false);
  let actionError = $state<unknown>(undefined);
  const isLoading = $derived(loading || busy);
  const shownError = $derived(actionError ?? error);

  async function handleConfirm() {
    if (isLoading) return;
    if (action) {
      busy = true;
      actionError = undefined;
      try {
        await action();
      } catch (e) {
        actionError = e;
        busy = false;
        return;
      }
      busy = false;
      open = false;
    }
    await onConfirm?.();
  }

  function cancel() {
    if (isLoading) return;
    open = false;
    onCancel?.();
  }

  const confirmText = $derived(
    confirmLabel ?? (variant === "danger" ? $_("ui.delete") : $_("ui.confirm")),
  );
</script>

{#if open}
  <Modal {title} size="md" dismissable={!isLoading} showClose={false} onClose={cancel}>
    {#snippet icon()}
      {#if variant === "danger"}
        <ExclamationCircleOutline size="lg" class="text-danger" />
      {:else}
        <QuestionCircleOutline size="lg" />
      {/if}
    {/snippet}

    <div class="space-y-4">
      {#if body}
        <p class="text-sm text-text-muted whitespace-pre-line">{body}</p>
      {/if}
      {@render children?.()}
      {#if shownError}
        <ErrorState compact error={shownError} />
      {/if}
    </div>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={cancel} disabled={isLoading}>
        {cancelLabel ?? $_("ui.cancel")}
      </button>
      <button
        type="button"
        class="app-btn {variant === 'danger' ? 'app-btn-danger' : 'app-btn-primary'}"
        onclick={() => void handleConfirm()}
        disabled={isLoading}
        aria-busy={isLoading}
        data-autofocus
      >
        {#if isLoading}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {loadingLabel ?? $_("ui.working")}
        {:else}
          {confirmText}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
