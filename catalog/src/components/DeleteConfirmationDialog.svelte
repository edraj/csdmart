<script lang="ts">
  import { Modal } from "flowbite-svelte";
  import { _ } from "@/i18n";
  import { MODAL_SIZE } from "@/lib/ui/modal-sizes";

  interface Props {
    open?: boolean;
    title?: string;
    itemName?: string;
    itemType?: string;
    isDeleting?: boolean;
    onConfirm?: () => void;
    onCancel?: () => void;
  }

  let {
    open = $bindable(false),
    title = "",
    itemName = "",
    itemType = "item",
    isDeleting = false,
    onConfirm = () => {},
    onCancel = () => {},
  }: Props = $props();

  function handleConfirm() {
    onConfirm();
  }

  function handleCancel() {
    onCancel();
  }

  const displayTitle = $derived(title || $_("delete_confirmation.title", { values: { type: itemType } }));
</script>

<Modal
  title={displayTitle}
  bind:open={open}
  size={MODAL_SIZE.form}
  class="bg-white dark:bg-white max-h-[90vh]"
  headerClass="text-gray-900 dark:text-gray-900"
  bodyClass="bg-white dark:bg-white text-gray-700 p-4 md:p-5 space-y-4 overflow-y-auto overscroll-contain max-h-[70vh]"
  footerClass="bg-white dark:bg-white flex items-center p-4 md:p-5 space-x-3 rtl:space-x-reverse rounded-b-lg shrink-0"
  placement="center"
  dismissable={!isDeleting}
  autoclose={false}
>
  <p class="text-sm text-gray-500 -mt-2 mb-4">
    {$_("delete_confirmation.irreversible")}
  </p>

  <div class="flex items-start gap-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3">
    <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-red-100 text-red-600">
      <svg class="h-5 w-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
      </svg>
    </div>
    <div class="flex-1">
      <h4 class="text-sm font-semibold text-red-800">
        {$_("delete_confirmation.confirm")}
      </h4>
      <p class="mt-1 text-sm text-red-700">
        {$_("delete_confirmation.warning")}
      </p>
    </div>
  </div>

  <div class="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm">
    <span class="font-semibold text-gray-700">{$_("delete_confirmation.item_label")}:</span>
    <span class="ml-1 break-all text-gray-900">{itemName}</span>
    {#if itemType}
      <span class="ml-2 inline-flex items-center rounded-md bg-gray-200 px-2 py-0.5 text-[11px] font-medium uppercase tracking-wide text-gray-600">
        {itemType}
      </span>
    {/if}
  </div>

  {#snippet footer()}
    <button
      onclick={handleCancel}
      class="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
      disabled={isDeleting}
    >
      {$_("cancel")}
    </button>
    <button
      onclick={handleConfirm}
      disabled={isDeleting}
      class="inline-flex items-center gap-2 rounded-lg bg-red-600 px-4 py-2 text-sm font-semibold text-white hover:bg-red-700 disabled:opacity-60"
    >
      {#if isDeleting}
        <span class="h-4 w-4 animate-spin rounded-full border-2 border-white/40 border-t-white"></span>
        {$_("deleting")}
      {:else}
        {$_("delete")}
      {/if}
    </button>
  {/snippet}
</Modal>

