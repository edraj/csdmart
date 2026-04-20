<script lang="ts">
  import { createEventDispatcher } from "svelte";
  import { _, locale } from "@/i18n";
  import { createReport } from "@/lib/dmart_services";
  import {
    successToastMessage,
    errorToastMessage,
  } from "@/lib/toasts_messages";
  import { params } from "@roxi/routify";
  import ReportThankYouModal from "./ReportThankYouModal.svelte";

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

  function handleBackdropClick(event: any) {
    if (event.target === event.currentTarget) {
      closeModal();
    }
  }

  function handleKeydown(event: any) {
    if (event.key === "Escape") {
      closeModal();
    }
  }
</script>

{#if isVisible}
  <!-- svelte-ignore a11y-click-events-have-key-events -->
  <!-- svelte-ignore a11y-no-static-element-interactions -->
  <div
    class="modal-backdrop"
    on:click={handleBackdropClick}
    on:keydown={handleKeydown}
  >
    <div class="modal-container">
      <div class="modal-header">
        <h2 class="modal-title">{$_("reports.modal.title")}</h2>
        <button
          type="button"
          class="close-button"
          on:click={closeModal}
          aria-label={$_("common.close")}
        >
          <svg
            class="close-icon"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M6 18L18 6M6 6l12 12"
            />
          </svg>
        </button>
      </div>

      <div class="modal-body">
        <!-- Reporting entry info -->
        <div class="reporting-info">
          <h3 class="info-title">{$_("reports.modal.reporting_entry")}</h3>
          <p class="entry-info">
            <span class="entry-title">{entryTitle}</span>
            <span class="entry-id">({entryShortname})</span>
          </p>
        </div>

        <!-- Report form -->
        <form on:submit|preventDefault={submitReport} class="report-form">
          <!-- Report Type -->
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

          <!-- Report Title -->
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

          <!-- Report Description -->
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

          <!-- Actions -->
          <div class="modal-actions">
            <button
              type="button"
              class="cancel-button"
              on:click={closeModal}
              disabled={isSubmitting}
            >
              {$_("common.cancel")}
            </button>
            <button
              type="submit"
              class="submit-button"
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
          </div>
        </form>
      </div>
    </div>
  </div>
{/if}

<ReportThankYouModal
  show={showThankYouModal}
  onClose={() => (showThankYouModal = false)}
/>

<style>
  .modal-backdrop {
    position: fixed;
    inset: 0;
    background-color: rgba(0, 0, 0, 0.4);
    backdrop-filter: blur(4px);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
    padding: 1rem;
    animation: fadeIn var(--duration-fast) var(--ease-out);
  }

  .modal-container {
    background: var(--surface-card);
    border-radius: var(--radius-xl);
    box-shadow: var(--shadow-xl);
    max-width: 32rem;
    width: 100%;
    max-height: 90vh;
    overflow-y: auto;
    animation: scaleIn var(--duration-normal) var(--ease-out);
  }

  .modal-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1.5rem 1.5rem 1rem 1.5rem;
    border-bottom: 1px solid var(--color-gray-200);
  }

  .modal-title {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-gray-900);
    margin: 0;
  }

  .close-button {
    background: none;
    border: none;
    color: var(--color-gray-500);
    cursor: pointer;
    padding: 0.25rem;
    border-radius: var(--radius-sm);
    transition: background var(--duration-fast) ease, color var(--duration-fast) ease;
  }

  .close-button:hover {
    color: var(--color-gray-700);
    background-color: var(--color-gray-100);
  }

  .close-icon {
    width: 1.5rem;
    height: 1.5rem;
  }

  .modal-body {
    padding: 1.5rem;
  }

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

  .modal-actions {
    display: flex;
    gap: 0.75rem;
    justify-content: flex-end;
    margin-top: 1.5rem;
  }

  .cancel-button,
  .submit-button {
    padding: 0.75rem 1.5rem;
    border-radius: var(--radius-lg);
    font-size: 0.875rem;
    font-weight: 500;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .cancel-button {
    background-color: var(--surface-card);
    color: var(--color-gray-700);
    border: 1px solid var(--color-gray-300);
  }

  .cancel-button:hover:not(:disabled) {
    background-color: var(--color-gray-50);
  }

  .submit-button {
    background-color: var(--color-error);
    color: white;
    border: 1px solid var(--color-error);
  }

  .submit-button:hover:not(:disabled) {
    background-color: #b91c1c;
    border-color: #b91c1c;
    transform: translateY(-1px);
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

  @media (max-width: 640px) {
    .modal-container {
      margin: 0.5rem;
      max-height: calc(100vh - 1rem);
    }

    .modal-header,
    .modal-body {
      padding: 1rem;
    }

    .modal-actions {
      flex-direction: column;
    }

    .cancel-button,
    .submit-button {
      width: 100%;
      justify-content: center;
    }
  }
</style>
