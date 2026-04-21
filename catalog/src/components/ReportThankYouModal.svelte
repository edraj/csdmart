<script lang="ts">
  import { _ } from "@/i18n";
  import Modal from "./Modal.svelte";
  import { CheckCircleSolid } from "flowbite-svelte-icons";

  export let show = false;
  export let onClose: () => void = () => {};

  function handleClose() {
    show = false;
    onClose();
  }
</script>

{#if show}
  <Modal
    onClose={handleClose}
    title={$_("reports.modal.thank_you")}
    ariaLabel={$_("reports.modal.thank_you")}
    size="md"
  >
    {#snippet icon()}
      <CheckCircleSolid class="w-6 h-6 text-green-600" />
    {/snippet}

    <div class="success-content">
      <div class="success-icon-container">
        <svg
          class="success-icon"
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path
            stroke-linecap="round"
            stroke-linejoin="round"
            stroke-width="2"
            d="M5 13l4 4L19 7"
          />
        </svg>
      </div>

      <h3 class="success-title">{$_("reports.modal.submitted_successfully")}</h3>
      <p class="success-message">
        {$_("reports.modal.thanks_message")}
      </p>
    </div>

    {#snippet footer()}
      <button type="button" class="submit-button" on:click={handleClose}>
        {$_("common.got_it")}
      </button>
    {/snippet}
  </Modal>
{/if}

<style>
  .success-content {
    text-align: center;
    padding: 1rem 0;
  }

  .success-icon-container {
    width: 3.5rem;
    height: 3.5rem;
    background-color: #ecfdf5;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.25rem auto;
  }

  .success-icon {
    width: 1.75rem;
    height: 1.75rem;
    color: #10b981;
  }

  .success-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: #111827;
    margin: 0 0 0.75rem 0;
  }

  .success-message {
    font-size: 0.875rem;
    color: #6b7280;
    line-height: 1.5;
    margin: 0;
  }

  .submit-button {
    padding: 0.625rem 1.5rem;
    background-color: #4f46e5;
    color: white;
    border: 1px solid #4f46e5;
    border-radius: 0.75rem;
    font-size: 0.875rem;
    font-weight: 600;
    cursor: pointer;
    transition: all 0.2s;
  }

  .submit-button:hover {
    background-color: #4338ca;
    border-color: #4338ca;
  }
</style>
