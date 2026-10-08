<script lang="ts">
  import { _ } from "@/i18n";
  import Modal from "./Modal.svelte";
  import { CheckCircleOutline, CheckCircleSolid } from "flowbite-svelte-icons";

  let {
    show = $bindable(false),
    onClose = () => {},
  }: { show?: boolean; onClose?: () => void } = $props();

  function handleClose() {
    show = false;
    onClose();
  }
</script>

{#if show}
  <Modal onClose={handleClose} title={$_("reports.modal.thank_you")} ariaLabel={$_("reports.modal.thank_you")} size="md">
    {#snippet icon()}
      <CheckCircleSolid class="w-6 h-6 text-success" />
    {/snippet}

    <div class="flex flex-col items-center text-center py-2">
      <div class="w-14 h-14 rounded-full flex items-center justify-center bg-success-soft text-success mb-4" aria-hidden="true">
        <CheckCircleOutline size="lg" />
      </div>
      <h3 class="text-lg font-semibold text-text">{$_("reports.modal.submitted_successfully")}</h3>
      <p class="mt-2 text-sm text-text-muted leading-relaxed">{$_("reports.modal.thanks_message")}</p>
    </div>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-primary" onclick={handleClose} data-autofocus>
        {$_("common.got_it")}
      </button>
    {/snippet}
  </Modal>
{/if}
