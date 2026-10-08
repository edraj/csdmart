<script lang="ts">
  import { onMount } from "svelte";
  import { _ } from "@/i18n";
  import { DmartScope } from "@edraj/tsdmart";
  import {
    checkApplicationsFolders,
    createApplicationsFolders,
    ensureCriticalResources,
    checkCriticalResources,
  } from "@/lib/dmart_services";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import {
    CheckCircleOutline,
    CogOutline,
    ExclamationCircleOutline,
    PlusOutline,
    RefreshOutline,
  } from "flowbite-svelte-icons";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  let missingFolders = $state<string[]>([]);
  let missingWorkflowSchema = $state(false);
  let missingCriticalResources = $state<string[]>([]);

  let checking = $state(true);
  let fixing = $state(false);
  let fixed = $state(false);
  let workflowSchemaCreated = $state(false);

  $effect(() => setTitle($_("admin_settings.title")));

  let hasIssues = $derived(
    missingFolders.length > 0 ||
      missingWorkflowSchema ||
      missingCriticalResources.length > 0,
  );

  async function runCheck() {
    checking = true;
    fixed = false;
    try {
      const [result, criticalResult] = await Promise.all([
        checkApplicationsFolders(DmartScope.managed),
        checkCriticalResources(),
      ]);

      if (!result.exists && result.error !== "permission_denied") {
        missingFolders = result.missing || [];
        missingWorkflowSchema = result.missingWorkflowSchema || false;
      } else {
        missingFolders = [];
        missingWorkflowSchema = false;
      }
      missingCriticalResources = criticalResult.missing || [];
    } catch (err) {
      log.error("Error checking resources:", err);
      missingFolders = [];
      missingWorkflowSchema = false;
      missingCriticalResources = [];
    } finally {
      checking = false;
    }
  }

  async function runFix() {
    fixing = true;
    try {
      const [result] = await Promise.all([
        createApplicationsFolders(DmartScope.managed),
        ensureCriticalResources(),
      ]);
      if (result.success) {
        fixed = true;
        missingFolders = [];
        missingWorkflowSchema = false;
        missingCriticalResources = [];
        workflowSchemaCreated = result.workflowSchemaCreated || false;
      } else {
        missingFolders = result.failed || [];
        missingWorkflowSchema = result.workflowSchemaFailed || false;
      }
    } catch (err) {
      log.error("Error fixing resources:", err);
    } finally {
      fixing = false;
    }
  }

  onMount(runCheck);
</script>

<div class="mx-auto max-w-4xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("admin_settings.title")}
    description={$_("admin_settings.subtitle")}
    icon={CogOutline}
    backHref="/dashboard/admin"
    backLabel={$_("admin_settings.back_to_dashboard")}
  >
    {#snippet actions()}
      <button
        type="button"
        class="app-btn app-btn-secondary app-btn-sm"
        onclick={runCheck}
        disabled={checking || fixing}
      >
        <RefreshOutline size="sm" class={checking ? "animate-spin" : ""} aria-hidden="true" />
        {checking ? $_("admin_settings.checking") : $_("admin_settings.recheck")}
      </button>
    {/snippet}
  </PageHeader>

  <Card>
    <div class="flex flex-wrap items-center justify-between gap-3 mb-2">
      <h2 class="text-lg font-semibold text-text">
        {$_("admin_settings.critical_resources.title")}
      </h2>
      {#if !checking && !hasIssues && !fixed}
        <Badge variant="success" size="sm">
          <span class="w-1.5 h-1.5 rounded-full bg-current" aria-hidden="true"></span>
          {$_("admin_settings.critical_resources.status_ok")}
        </Badge>
      {:else if !checking && hasIssues}
        <Badge variant="warning" size="sm">
          <span class="w-1.5 h-1.5 rounded-full bg-current" aria-hidden="true"></span>
          {$_("admin_settings.critical_resources.status_issues")}
        </Badge>
      {/if}
    </div>
    <p class="text-sm text-text-muted mb-6">
      {$_("admin_settings.critical_resources.description")}
    </p>

    {#if checking}
      <LoadingState label={$_("admin_settings.checking_resources")} class="py-4" />
    {:else if hasIssues}
      <div class="rounded-card border border-warning/40 bg-warning-soft p-4" role="status">
        <div class="flex items-start gap-3">
          <span class="shrink-0 mt-0.5 text-warning" aria-hidden="true">
            <ExclamationCircleOutline size="md" />
          </span>
          <div class="flex-1 min-w-0">
            <h3 class="text-sm font-semibold text-text mb-1">
              {$_("admin_settings.issues.heading")}
            </h3>
            <ul class="text-sm text-text-muted space-y-1 mb-4 list-disc ps-5">
              {#if missingFolders.length > 0}
                <li>
                  {$_("admin_settings.issues.missing_folders")}:
                  <span class="font-medium text-text">{missingFolders.join(", ")}</span>
                </li>
              {/if}
              {#if missingWorkflowSchema}
                <li>{$_("admin_settings.issues.missing_workflow_schema")}</li>
              {/if}
              {#if missingCriticalResources.length > 0}
                <li>
                  {$_("admin_settings.issues.missing_critical")}:
                  <span class="font-medium text-text">{missingCriticalResources.join(", ")}</span>
                </li>
              {/if}
            </ul>
            <p class="text-xs text-text-muted mb-4">
              {$_("admin_settings.issues.required_note")}
            </p>
            <button
              type="button"
              class="app-btn app-btn-primary app-btn-sm"
              onclick={runFix}
              disabled={fixing}
              aria-busy={fixing}
            >
              {#if fixing}
                <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
                {$_("admin_settings.issues.fixing")}
              {:else}
                <PlusOutline size="sm" aria-hidden="true" />
                {$_("admin_settings.issues.fix_button")}
              {/if}
            </button>
          </div>
        </div>
      </div>
    {:else}
      <div class="rounded-card border border-success/40 bg-success-soft p-4" role="status">
        <div class="flex items-start gap-3">
          <span class="shrink-0 mt-0.5 text-success" aria-hidden="true">
            <CheckCircleOutline size="md" />
          </span>
          <div>
            <h3 class="text-sm font-semibold text-text mb-1">
              {fixed ? $_("admin_settings.fixed.heading") : $_("admin_settings.healthy.heading")}
            </h3>
            <p class="text-sm text-text-muted">
              {fixed ? $_("admin_settings.fixed.body") : $_("admin_settings.healthy.body")}
              {#if fixed && workflowSchemaCreated}
                <br /><span class="font-medium text-text">workflow</span>
                {$_("admin_settings.fixed.workflow_schema_created")}
              {/if}
            </p>
          </div>
        </div>
      </div>
    {/if}
  </Card>
</div>
