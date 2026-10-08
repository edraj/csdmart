<script lang="ts">
  import { onMount } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { getReports, getReportDetails, replyToReport } from "@/lib/dmart_services";
  import { getWorkflow } from "@/lib/dmart_services/workflows";
  import { toasts } from "@/lib/toast";
  import { FlagOutline, InboxOutline, PaperPlaneOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar from "@/components/ui/CatalogToolbar.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface Reply {
    timestamp: string;
    admin_shortname: string;
    reply: string;
  }

  interface ReportData {
    title: string;
    description: string;
    report_type?: string;
    reported_entry?: string;
    reported_entry_title?: string;
    reported_space?: string;
    reported_subpath?: string;
    created_at?: string;
    replies: Reply[];
  }

  interface Report {
    shortname: string;
    attributes: { state?: string; owner_shortname?: string; created_at?: string; [key: string]: unknown };
    attachments?: unknown;
    reportData: ReportData;
  }

  interface WorkflowState {
    state: string;
    name?: string;
    next?: Array<{ action: string; state?: string }>;
  }

  interface Workflow {
    initial_state?: Array<{ name?: string; state?: string }>;
    states?: WorkflowState[];
  }

  let reports = $state<Report[]>([]);
  let isLoading = $state(true);
  let loadError = $state<unknown>(null);
  let selectedReport = $state<Report | null>(null);
  let showReplyModal = $state(false);
  let adminReply = $state("");
  let isSubmittingReply = $state(false);
  let selectedAction = $state("no_action");
  let workflow = $state<Workflow | null>(null);
  let availableTransitions = $state<Array<{ action: string }>>([]);
  let selectedStatusFilter = $state("all");

  $effect(() => setTitle($_("reports.admin.title")));

  // Labels resolve at render time so a language switch re-labels the filter.
  const statusFilters = $derived.by(() => {
    const filters: Array<{ value: string; label: string }> = [{ value: "all", label: $_("reports.admin.filters.all") }];
    for (const s of workflow?.initial_state ?? []) {
      const value = s.name ?? s.state;
      if (value && !filters.some((f) => f.value === value)) filters.push({ value, label: value });
    }
    for (const s of workflow?.states ?? []) {
      if (!filters.some((f) => f.value === s.state)) filters.push({ value: s.state, label: s.name || s.state });
    }
    return filters;
  });

  onMount(async () => {
    await Promise.all([loadReports(), loadWorkflow()]);
  });

  async function loadWorkflow() {
    try {
      const response = await getWorkflow("report_workflow", "catalog");
      if (response?.payload?.body) workflow = response.payload.body as Workflow;
    } catch (err) {
      log.error("Error loading workflow:", err);
    }
  }

  function getRepliesFromAttachments(attachments: unknown): Reply[] {
    if (!attachments) return [];
    let all: Array<Record<string, unknown>> = [];
    if (Array.isArray(attachments)) {
      all = attachments as Array<Record<string, unknown>>;
    } else if (typeof attachments === "object") {
      for (const val of Object.values(attachments as Record<string, unknown>)) {
        if (Array.isArray(val)) all.push(...(val as Array<Record<string, unknown>>));
        else if (val && typeof val === "object") all.push(val as Record<string, unknown>);
      }
    }
    return all
      .filter((a) => a.resource_type === "comment")
      .map((a) => {
        const attrs = (a.attributes ?? {}) as Record<string, unknown>;
        const payload = (attrs.payload ?? (a.payload as unknown)) as { body?: { body?: unknown } } | undefined;
        return {
          timestamp: String(attrs.created_at ?? a.created_at ?? ""),
          admin_shortname: String(attrs.owner_shortname ?? a.owner_shortname ?? ""),
          reply: typeof payload?.body?.body === "string" ? payload.body.body : "",
        };
      });
  }

  function toReport(raw: Record<string, unknown>): Report {
    const attributes = (raw.attributes ?? {}) as Report["attributes"];
    const body = ((attributes.payload as { body?: Record<string, unknown> } | undefined)?.body ?? {}) as Record<string, unknown>;
    const displayname = attributes.displayname as Record<string, string> | undefined;
    const description = attributes.description as Record<string, string> | undefined;
    const str = (v: unknown) => (typeof v === "string" ? v : undefined);
    return {
      shortname: String(raw.shortname),
      attributes,
      attachments: raw.attachments,
      reportData: {
        title: str(body.title) ?? displayname?.en ?? "",
        description: str(body.description) ?? description?.en ?? "",
        report_type: str(body.report_type),
        reported_entry: str(body.entry) ?? str(body.reported_entry),
        reported_entry_title: str(body.reported_entry_title),
        reported_space: str(body.space_name) ?? str(body.reported_space),
        reported_subpath: str(body.subpath) ?? str(body.reported_subpath),
        created_at: str(body.created_at),
        replies: getRepliesFromAttachments(raw.attachments),
      },
    };
  }

  async function loadReports() {
    try {
      isLoading = true;
      loadError = null;
      const response = await getReports(selectedStatusFilter === "all" ? undefined : selectedStatusFilter);
      reports = ((response?.records ?? []) as unknown as Array<Record<string, unknown>>).map(toReport);
    } catch (err) {
      log.error("Error loading reports:", err);
      loadError = err;
    } finally {
      isLoading = false;
    }
  }

  function stateOf(report: Report): string {
    return report.attributes?.state || "Pending";
  }

  function isInitialState(report: Report): boolean {
    const state = stateOf(report).toLowerCase();
    if (!workflow?.initial_state) return state === "pending";
    return workflow.initial_state.some((s) => s.name?.toLowerCase() === state || s.state?.toLowerCase() === state);
  }

  function workflowStateOf(report: Report): WorkflowState | undefined {
    const state = stateOf(report).toLowerCase();
    return workflow?.states?.find((s) => s.state?.toLowerCase() === state);
  }

  function isEndState(report: Report): boolean {
    const s = workflowStateOf(report);
    return !!s && (!s.next || s.next.length === 0);
  }

  function stateLabel(report: Report): string {
    return workflowStateOf(report)?.name || stateOf(report);
  }

  function stateTone(report: Report): "neutral" | "danger" | "success" {
    if (isInitialState(report)) return "neutral";
    if (isEndState(report)) return "danger";
    return workflowStateOf(report) ? "success" : "neutral";
  }

  async function openReplyModal(report: Report) {
    try {
      const detailed = (await getReportDetails(report.shortname)) as unknown as Record<string, unknown> | null;
      selectedReport = detailed ? toReport({ ...detailed, shortname: report.shortname }) : report;
      showReplyModal = true;
      adminReply = "";
      selectedAction = "no_action";
      availableTransitions = workflowStateOf(selectedReport)?.next ?? [];
    } catch (err) {
      log.error("Error loading report details:", err);
      toasts.error($_("reports.admin.error.loading_details_failed"));
    }
  }

  function closeReplyModal() {
    if (isSubmittingReply) return;
    showReplyModal = false;
    selectedReport = null;
    adminReply = "";
    selectedAction = "no_action";
  }

  async function submitReply(event: SubmitEvent) {
    event.preventDefault();
    if (!selectedReport) return;
    if (!adminReply.trim()) {
      toasts.error($_("reports.admin.validation.reply_required"));
      return;
    }

    try {
      isSubmittingReply = true;
      const success = await replyToReport(
        selectedReport.shortname,
        adminReply,
        selectedAction !== "no_action" ? selectedAction : undefined,
      );
      isSubmittingReply = false;
      if (success) {
        toasts.success($_("reports.admin.success.reply_sent"));
        closeReplyModal();
        await loadReports();
      } else {
        toasts.error($_("reports.admin.error.reply_failed"));
      }
    } catch (err) {
      log.error("Error submitting reply:", err);
      toasts.error($_("reports.admin.error.reply_failed"));
    } finally {
      isSubmittingReply = false;
    }
  }

  function changeFilter(value: string) {
    selectedStatusFilter = value;
    loadReports();
  }
</script>

<div class="mx-auto max-w-6xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("reports.admin.title")} description={$_("reports.admin.description")} icon={FlagOutline} />

  <CatalogToolbar class="mb-6">
    {#snippet filters()}
      <label for="status-filter" class="text-sm text-text-muted">{$_("reports.admin.filter_by_status")}</label>
      <select
        id="status-filter"
        value={selectedStatusFilter}
        onchange={(e) => changeFilter((e.currentTarget as HTMLSelectElement).value)}
        class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
      >
        {#each statusFilters as filter (filter.value)}
          <option value={filter.value}>{filter.label}</option>
        {/each}
      </select>
    {/snippet}
  </CatalogToolbar>

  {#if isLoading && reports.length === 0}
    <LoadingState label={$_("reports.admin.loading")} />
  {:else if loadError}
    <ErrorState title={$_("reports.admin.error.loading_failed")} error={loadError} onRetry={loadReports} />
  {:else if reports.length === 0}
    <EmptyState
      icon={InboxOutline}
      title={$_("reports.admin.empty.title")}
      hint={selectedStatusFilter === "all" ? $_("reports.admin.empty.no_reports") : $_("reports.admin.empty.no_reports_filter")}
    >
      {#if selectedStatusFilter !== "all"}
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => changeFilter("all")}>
          {$_("reports.admin.actions.clear_filters")}
        </button>
      {/if}
    </EmptyState>
  {:else}
    <LoadingState variant="overlay" loading={isLoading}>
      <ul class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6 list-none p-0 m-0">
        {#each reports as report (report.shortname)}
          {@const ended = isEndState(report)}
          <li class="flex">
            <Card class="w-full flex flex-col">
              <div class="flex items-start justify-between gap-2 mb-3">
                <span class="text-sm font-semibold text-text capitalize">
                  {report.reportData.report_type?.replace(/_/g, " ") || $_("reports.admin.type_general")}
                </span>
                <Badge variant={stateTone(report)} size="sm">{stateLabel(report)}</Badge>
              </div>

              <h3 class="text-lg font-semibold text-text mb-1">{report.reportData.title || $_("reports.admin.untitled")}</h3>
              <p class="text-sm text-text-muted mb-4 line-clamp-3">{report.reportData.description}</p>

              <div class="rounded-control bg-surface-3 p-3 mb-4">
                <h4 class="text-xs font-medium text-text-faint mb-1">{$_("reports.admin.reported_entry")}</h4>
                <div class="flex flex-col gap-0.5 text-sm">
                  <span class="font-semibold text-text break-words">{report.reportData.reported_entry_title || report.reportData.reported_entry}</span>
                  {#if report.reportData.reported_entry_title}
                    <span class="text-xs text-text-faint break-all">({report.reportData.reported_entry})</span>
                  {/if}
                  {#if report.reportData.reported_space}
                    <span class="text-xs text-text-faint">
                      {$_("reports.admin.in_space", { values: { space: report.reportData.reported_space } })}
                    </span>
                  {/if}
                </div>
              </div>

              <dl class="text-xs text-text-faint space-y-1 mb-4">
                <div class="flex gap-1">
                  <dt>{$_("reports.admin.reported_by")}:</dt>
                  <dd class="font-semibold text-text-muted">{report.attributes.owner_shortname}</dd>
                </div>
                <div class="flex gap-1 tabular-nums">
                  <dt>{$_("reports.admin.reported_at")}:</dt>
                  <dd class="text-text-muted">
                    {formatDate(report.reportData.created_at || report.attributes.created_at, "relative", $locale)}
                  </dd>
                </div>
              </dl>

              {#if report.reportData.replies.length > 0}
                <div class="mt-auto pt-4 border-t border-border">
                  <h4 class="text-xs font-semibold text-text mb-2">{$_("reports.admin.notes")}</h4>
                  <ul class="space-y-2 list-none p-0 m-0">
                    {#each report.reportData.replies as reply, i (i)}
                      <li class="rounded-control bg-surface-3 p-3">
                        <div class="flex justify-between items-center gap-2 mb-1">
                          <span class="text-xs font-semibold text-text">{reply.admin_shortname}</span>
                          <span class="text-xs text-text-faint tabular-nums">{formatDate(reply.timestamp, "relative", $locale)}</span>
                        </div>
                        <p class="text-xs text-text-muted m-0 whitespace-pre-wrap">{reply.reply || $_("reports.admin.no_content")}</p>
                      </li>
                    {/each}
                  </ul>
                </div>
              {/if}

              <div class="mt-auto pt-4 flex flex-wrap gap-2">
                {#if ended}
                  <span class="text-sm text-danger">{stateLabel(report)}</span>
                {:else}
                  <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={() => openReplyModal(report)}>
                    {$_("reports.admin.actions.reply")}
                  </button>
                {/if}
              </div>
            </Card>
          </li>
        {/each}
      </ul>
    </LoadingState>
  {/if}
</div>

{#if showReplyModal && selectedReport}
  {@const report = selectedReport}
  <Modal title={$_("reports.admin.reply_modal.title")} size="lg" dismissable={!isSubmittingReply} onClose={closeReplyModal}>
    <div class="rounded-control border border-border bg-surface-3 p-4 mb-5">
      <h3 class="text-base font-semibold text-text mb-1">{report.reportData.title}</h3>
      <p class="text-sm text-text-muted mb-3">{report.reportData.description}</p>
      <dl class="text-sm text-text-muted space-y-1">
        <div class="flex gap-1">
          <dt class="font-medium text-text">{$_("reports.admin.reported_entry")}:</dt>
          <dd>{report.reportData.reported_entry_title || report.reportData.reported_entry}</dd>
        </div>
        {#if report.reportData.report_type}
          <div class="flex gap-1">
            <dt class="font-medium text-text">{$_("reports.modal.report_type")}:</dt>
            <dd class="capitalize">{report.reportData.report_type.replace(/_/g, " ")}</dd>
          </div>
        {/if}
      </dl>
    </div>

    <form id="reply-form" onsubmit={submitReply} class="space-y-4">
      <div>
        <label for="adminReply" class="block text-sm font-medium text-text mb-1.5">
          {$_("reports.admin.reply_modal.your_notes")} <span class="text-danger" aria-hidden="true">*</span>
        </label>
        <textarea
          id="adminReply"
          bind:value={adminReply}
          class="w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary resize-y min-h-24"
          placeholder={$_("reports.admin.reply_modal.notes_placeholder")}
          rows="4"
          required
          data-autofocus
        ></textarea>
      </div>

      <div>
        <label for="actionSelect" class="block text-sm font-medium text-text mb-1.5">
          {$_("reports.admin.reply_modal.action")}
        </label>
        <select
          id="actionSelect"
          bind:value={selectedAction}
          class="w-full h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
        >
          <option value="no_action">{$_("reports.admin.actions.no_action")}</option>
          {#each availableTransitions as transition (transition.action)}
            <option value={transition.action}>{transition.action}</option>
          {/each}
        </select>
      </div>
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeReplyModal} disabled={isSubmittingReply}>
        {$_("common.cancel")}
      </button>
      <button
        type="submit"
        form="reply-form"
        class="app-btn app-btn-primary"
        disabled={isSubmittingReply || !adminReply.trim()}
        aria-busy={isSubmittingReply}
      >
        {#if isSubmittingReply}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("reports.admin.reply_modal.sending")}
        {:else}
          <PaperPlaneOutline size="sm" aria-hidden="true" />
          {$_("reports.admin.reply_modal.send_reply")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
