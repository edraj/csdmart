<script lang="ts">
  import { _ } from "@/i18n";
  import { createReport } from "@/lib/dmart_services";
  import { toasts } from "@/lib/toast";
  import ReportThankYouModal from "./ReportThankYouModal.svelte";
  import Modal from "./Modal.svelte";
  import { FlagSolid } from "flowbite-svelte-icons";

  interface Props {
    isVisible?: boolean;
    entryShortname?: string;
    entryTitle?: string;
    spaceName?: string;
    subpath?: string;
    onClose?: () => void;
    onReportSubmitted?: () => void;
  }

  let {
    isVisible = $bindable(false),
    entryShortname = "",
    entryTitle = "",
    spaceName = "",
    subpath = "",
    onClose = () => {},
    onReportSubmitted = () => {},
  }: Props = $props();

  let isSubmitting = $state(false);
  let reportTitle = $state("");
  let reportDescription = $state("");
  let showThankYouModal = $state(false);
  let selectedReportType = $state("other");

  const uid = $props.id();
  const DESCRIPTION_MAX = 1000;

  // $derived so the labels follow a locale switch while the modal is open.
  const reportTypes = $derived([
    { value: "inappropriate_content", label: $_("reports.types.inappropriate_content") },
    { value: "spam", label: $_("reports.types.spam") },
    { value: "misinformation", label: $_("reports.types.misinformation") },
    { value: "copyright_violation", label: $_("reports.types.copyright_violation") },
    { value: "harassment", label: $_("reports.types.harassment") },
    { value: "other", label: $_("reports.types.other") },
  ]);

  const canSubmit = $derived(!isSubmitting && reportTitle.trim() !== "" && reportDescription.trim() !== "");

  function closeModal() {
    isVisible = false;
    resetForm();
    onClose();
  }

  function resetForm() {
    reportTitle = "";
    reportDescription = "";
    selectedReportType = "other";
    isSubmitting = false;
  }

  async function submitReport() {
    if (!reportTitle.trim() || !reportDescription.trim()) {
      toasts.error($_("reports.validation.required_fields"));
      return;
    }

    isSubmitting = true;
    try {
      const success = await createReport({
        title: reportTitle,
        description: reportDescription,
        reported_entry: entryShortname,
        reported_entry_title: entryTitle,
        space_name: spaceName,
        subpath: subpath,
        report_type: selectedReportType,
        status: "pending",
        type: "ticket",
      });

      if (success) {
        closeModal();
        showThankYouModal = true;
        onReportSubmitted();
      } else {
        toasts.error($_("reports.error.submission_failed"));
      }
    } catch (error) {
      console.error("Error submitting report:", error);
      toasts.error($_("reports.error.submission_failed"));
    } finally {
      isSubmitting = false;
    }
  }
</script>

{#if isVisible}
  <Modal onClose={closeModal} title={$_("reports.modal.title")} ariaLabel={$_("reports.modal.title")} size="lg">
    {#snippet icon()}
      <FlagSolid class="w-6 h-6" />
    {/snippet}

    <div class="rounded-card border border-border bg-surface px-4 py-3 mb-5">
      <p class="text-sm font-semibold text-text-muted">{$_("reports.modal.reporting_entry")}</p>
      <p class="mt-1 text-sm text-text break-words">
        <span class="font-medium">{entryTitle}</span>
        <span class="text-text-faint">({entryShortname})</span>
      </p>
    </div>

    <form
      id="{uid}-report-form"
      onsubmit={(e) => {
        e.preventDefault();
        void submitReport();
      }}
      class="flex flex-col gap-5"
    >
      <div class="flex flex-col gap-1.5">
        <label for="{uid}-type" class="text-sm font-medium text-text">{$_("reports.modal.report_type")}</label>
        <select id="{uid}-type" bind:value={selectedReportType} class="field" required>
          {#each reportTypes as type (type.value)}
            <option value={type.value}>{type.label}</option>
          {/each}
        </select>
      </div>

      <div class="flex flex-col gap-1.5">
        <label for="{uid}-title" class="text-sm font-medium text-text">
          {$_("reports.modal.report_title")}
          <span class="text-danger" aria-hidden="true">*</span>
        </label>
        <input
          id="{uid}-title"
          type="text"
          bind:value={reportTitle}
          class="field"
          placeholder={$_("reports.modal.title_placeholder")}
          required
          maxlength="200"
        />
      </div>

      <div class="flex flex-col gap-1.5">
        <label for="{uid}-description" class="text-sm font-medium text-text">
          {$_("reports.modal.description")}
          <span class="text-danger" aria-hidden="true">*</span>
        </label>
        <textarea
          id="{uid}-description"
          bind:value={reportDescription}
          class="field resize-y min-h-24"
          placeholder={$_("reports.modal.description_placeholder")}
          required
          rows="4"
          maxlength={DESCRIPTION_MAX}
        ></textarea>
        <p class="text-end text-xs text-text-faint tabular-nums">{reportDescription.length}/{DESCRIPTION_MAX}</p>
      </div>
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeModal} disabled={isSubmitting}>
        {$_("common.cancel")}
      </button>
      <button type="submit" form="{uid}-report-form" class="app-btn app-btn-danger" disabled={!canSubmit} aria-busy={isSubmitting}>
        {#if isSubmitting}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("reports.modal.submitting")}
        {:else}
          {$_("reports.modal.submit_report")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

<ReportThankYouModal show={showThankYouModal} onClose={() => (showThankYouModal = false)} />

<style>
  .field {
    width: 100%;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    padding: 0.625rem 0.75rem;
    font-size: var(--font-size-sm);
    font-family: inherit;
    background: var(--color-surface-2);
    color: var(--color-text);
    transition: border-color var(--duration-fast) var(--ease-out), box-shadow var(--duration-fast) var(--ease-out);
  }

  .field::placeholder {
    color: var(--color-text-faint);
  }

  .field:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 1px var(--color-primary);
  }
</style>
