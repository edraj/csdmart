<script lang="ts">
  import { createEventDispatcher } from "svelte";
  import { _ } from "@/i18n";
  import { createReport } from "@/lib/dmart_services";
  import {
    successToastMessage,
    errorToastMessage,
  } from "@/lib/toasts_messages";
  import ReportThankYouModal from "./ReportThankYouModal.svelte";
  import Modal from "./Modal.svelte";
  import { FlagSolid } from "flowbite-svelte-icons";

  const dispatch = createEventDispatcher();

  export let isVisible = false;
  export let entryShortname = "";
  export let entryTitle = "";
  export let spaceName = "";
  export let subpath = "";

  let isSubmitting = false;
  let reportTitle = "";
  let reportDescription = "";
  let showThankYouModal = false;

  const reportTypes = [
    {
      value: "inappropriate_content",
      label: $_("reports.types.inappropriate_content"),
    },
    { value: "spam", label: $_("reports.types.spam") },
    { value: "misinformation", label: $_("reports.types.misinformation") },
    {
      value: "copyright_violation",
      label: $_("reports.types.copyright_violation"),
    },
    { value: "harassment", label: $_("reports.types.harassment") },
    { value: "other", label: $_("reports.types.other") },
  ];

  let selectedReportType = "other";

  function closeModal() {
    isVisible = false;
    resetForm();
    dispatch("close");
  }

  function resetForm() {
    reportTitle = "";
    reportDescription = "";
    selectedReportType = "other";
    isSubmitting = false;
    showThankYouModal = false;
  }

  async function submitReport() {
    if (!reportTitle.trim() || !reportDescription.trim()) {
      errorToastMessage(
        $_("reports.validation.required_fields") ||
          "Please fill in all required fields"
      );
      return;
    }

    isSubmitting = true;

    try {
      const reportData = {
        title: reportTitle,
        description: reportDescription,
        reported_entry: entryShortname,
        reported_entry_title: entryTitle,
        space_name: spaceName,
        subpath: subpath,
        report_type: selectedReportType,
        status: "pending",
        type: "ticket",
      };

      const success = await createReport(reportData);

      if (success) {
        closeModal();
        showThankYouModal = true;
        dispatch("reportSubmitted");
      } else {
        errorToastMessage(
          $_("reports.error.submission_failed") || "Failed to submit report"
        );
      }
    } catch (error) {
      console.error("Error submitting report:", error);
      errorToastMessage(
        $_("reports.error.submission_failed") || "Failed to submit report"
      );
    } finally {
      isSubmitting = false;
    }
  }
</script>

{#if isVisible}
  <Modal
    onClose={closeModal}
    title={$_("reports.modal.title")}
    ariaLabel={$_("reports.modal.title")}
    size="lg"
  >
    {#snippet icon()}
      <FlagSolid class="w-6 h-6" />
    {/snippet}

    <div class="reporting-info">
      <h3 class="info-title">{$_("reports.modal.reporting_entry")}</h3>
      <p class="entry-info">
        <span class="entry-title">{entryTitle}</span>
        <span class="entry-id">({entryShortname})</span>
      </p>
    </div>

    <form on:submit|preventDefault={submitReport} class="report-form">
      <div class="form-group">
        <label for="reportType" class="form-label">
          {$_("reports.modal.report_type")}
        </label>
        <select
          id="reportType"
          bind:value={selectedReportType}
          class="form-select"
          required
        >
          {#each reportTypes as type}
            <option value={type.value}>{type.label}</option>
          {/each}
        </select>
      </div>

      <div class="form-group">
        <label for="reportTitle" class="form-label">
          {$_("reports.modal.report_title")}
          <span class="required">*</span>
        </label>
        <input
          id="reportTitle"
          type="text"
          bind:value={reportTitle}
          class="form-input"
          placeholder={$_("reports.modal.title_placeholder")}
          required
          maxlength="200"
        />
      </div>

      <div class="form-group">
        <label for="reportDescription" class="form-label">
          {$_("reports.modal.description")}
          <span class="required">*</span>
        </label>
        <textarea
          id="reportDescription"
          bind:value={reportDescription}
          class="form-textarea"
          placeholder={$_("reports.modal.description_placeholder")}
          required
          rows="4"
          maxlength="1000"
        ></textarea>
        <div class="character-count">
          {reportDescription.length}/1000
        </div>
      </div>
    </form>

    {#snippet footer()}
      <button
        type="button"
        class="cancel-button"
        on:click={closeModal}
        disabled={isSubmitting}
      >
        {$_("common.cancel")}
      </button>
      <button
        type="button"
        class="submit-button"
        on:click={submitReport}
        disabled={isSubmitting ||
          !reportTitle.trim() ||
          !reportDescription.trim()}
      >
        {#if isSubmitting}
          <svg class="spinner" viewBox="0 0 24 24">
            <circle
              cx="12"
              cy="12"
              r="10"
              stroke="currentColor"
              stroke-width="4"
              fill="none"
              opacity="0.25"
            />
            <path
              fill="currentColor"
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
              opacity="0.75"
            />
          </svg>
          {$_("reports.modal.submitting")}
        {:else}
          {$_("reports.modal.submit_report")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

<ReportThankYouModal
  show={showThankYouModal}
  onClose={() => (showThankYouModal = false)}
/>

<style>
  .reporting-info {
    background-color: var(--color-gray-50);
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-md);
    padding: 1rem;
    margin-bottom: 1.5rem;
  }

  .info-title {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-gray-700);
    margin: 0 0 0.5rem 0;
  }

  .entry-info {
    margin: 0;
    color: var(--color-gray-500);
  }

  .entry-title {
    font-weight: 500;
    color: var(--color-gray-900);
  }

  .entry-id {
    font-size: 0.875rem;
    color: var(--color-gray-400);
  }

  .report-form {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
  }

  .form-group {
    display: flex;
    flex-direction: column;
  }

  .form-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-gray-700);
    margin-bottom: 0.5rem;
  }

  .required {
    color: var(--color-error);
  }

  .form-input,
  .form-select,
  .form-textarea {
    border: 1px solid var(--color-gray-300);
    border-radius: var(--radius-md);
    padding: 0.75rem;
    font-size: 0.875rem;
    background: white;
    transition:
      border-color var(--duration-fast) ease,
      box-shadow var(--duration-fast) ease;
  }

  .form-input:focus,
  .form-select:focus,
  .form-textarea:focus {
    outline: none;
    border-color: var(--color-primary-400);
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  .form-textarea {
    resize: vertical;
    min-height: 4rem;
  }

  .character-count {
    text-align: right;
    font-size: 0.75rem;
    color: var(--color-gray-400);
    margin-top: 0.25rem;
  }

  .cancel-button,
  .submit-button {
    padding: 0.625rem 1.5rem;
    border-radius: 0.75rem;
    font-size: 0.875rem;
    font-weight: 600;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .cancel-button {
    background-color: transparent;
    color: var(--color-gray-600);
    border: 1px solid transparent;
  }

  .cancel-button:hover:not(:disabled) {
    background-color: var(--color-gray-100);
    color: var(--color-gray-900);
  }

  .submit-button {
    background-color: var(--color-error);
    color: white;
    border: 1px solid var(--color-error);
  }

  .submit-button:hover:not(:disabled) {
    background-color: #b91c1c;
    border-color: #b91c1c;
  }

  .submit-button:disabled,
  .cancel-button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .spinner {
    width: 1rem;
    height: 1rem;
    animation: spin 1s linear infinite;
  }

  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
</style>
