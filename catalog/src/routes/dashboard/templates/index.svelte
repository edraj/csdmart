<script lang="ts">
  import MarkdownEditor from "@/components/editors/MarkdownEditor.svelte";
  import { createTemplate, deleteTemplate, getAllTemplates, updateTemplates, getSpaces, getSpaceSchema } from "@/lib/dmart_services";
  import { APPLICATIONS_SPACE } from "@/lib/constants";
  import { DmartScope } from "@edraj/tsdmart";
  import { onMount } from "svelte";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { toasts } from "@/lib/toast";
  import { confirm } from "@/lib/confirm";
  import { params } from "@roxi/routify";
  import { EditOutline, FileLinesOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  interface TemplateRecord {
    uuid?: string;
    shortname: string;
    subpath: string;
    attributes: {
      space_name?: string;
      owner_shortname?: string;
      created_at?: string;
      updated_at?: string;
      payload?: { body?: { title?: string; content?: string; space_name?: string; schema_shortname?: string } };
    };
  }

  interface SchemaKey {
    name: string;
    type: string;
    title: string;
  }

  interface NamedRecord {
    shortname: string;
    attributes?: { displayname?: unknown; payload?: { body?: unknown } };
  }

  let templates = $state<TemplateRecord[]>([]);
  let isLoading = $state(true);
  let loadError = $state<unknown>(null);

  let showModal = $state(false);
  let editingTemplate = $state<TemplateRecord | null>(null);

  let templateName = $state("");
  let templateShortname = $state("");
  let content = $state("");
  let isSaving = $state(false);
  let saveError = $state("");

  // Optional fields
  let targetSpaceName = $state("");
  let schemaShortname = $state("");
  let showOptionalFields = $state(false);
  let availableSpaces = $state<NamedRecord[]>([]);
  let availableSchemas = $state<NamedRecord[]>([]);
  let loadingSpaces = $state(false);
  let loadingSchemas = $state(false);
  let schemaKeys = $state<SchemaKey[]>([]);

  $effect(() => setTitle($_("templates.title")));

  // Get space_name from query params (when coming from admin space selection)
  const querySpaceName = $derived(($params?.space_name as string | undefined) || "");
  const isSpaceLocked = $derived(!!querySpaceName);
  const saveSpace = $derived(schemaShortname && targetSpaceName ? targetSpaceName : APPLICATIONS_SPACE);
  const defaultContent = $derived(`# ${$_("templates.default_content_title")}\n\n${$_("templates.default_content_body")}`);

  onMount(async () => {
    await loadTemplates();
  });

  async function loadTemplates() {
    try {
      isLoading = true;
      loadError = null;
      const response = await getAllTemplates();
      if (response.status === "success") {
        templates = (response.records ?? []) as unknown as TemplateRecord[];
      } else {
        loadError = $_("templates.messages.load_failed");
      }
    } catch (error) {
      log.error("Error loading templates:", error);
      loadError = error;
    } finally {
      isLoading = false;
    }
  }

  async function loadSpaces() {
    loadingSpaces = true;
    try {
      const response = await getSpaces(false, DmartScope.managed, []);
      if (response.status === "success") {
        availableSpaces = (response.records ?? []) as unknown as NamedRecord[];
      }
    } catch (error) {
      log.error("Error loading spaces:", error);
    } finally {
      loadingSpaces = false;
    }
  }

  async function loadSchemasForSpace(spaceName: string) {
    if (!spaceName) {
      availableSchemas = [];
      return;
    }
    loadingSchemas = true;
    try {
      const response = await getSpaceSchema(spaceName, DmartScope.managed);
      availableSchemas = response.status === "success" ? ((response.records ?? []) as unknown as NamedRecord[]) : [];
    } catch (error) {
      log.error("Error loading schemas:", error);
      availableSchemas = [];
    } finally {
      loadingSchemas = false;
    }
  }

  function handleTargetSpaceChange(event: Event) {
    targetSpaceName = (event.currentTarget as HTMLSelectElement).value;
    schemaShortname = "";
    schemaKeys = [];
    if (targetSpaceName) loadSchemasForSpace(targetSpaceName);
    else availableSchemas = [];
  }

  function handleSchemaChange(event: Event) {
    schemaShortname = (event.currentTarget as HTMLSelectElement).value;
    schemaKeys = [];
    if (schemaShortname && targetSpaceName) {
      const selected = availableSchemas.find((s) => s.shortname === schemaShortname);
      extractSchemaKeys(selected?.attributes?.payload?.body);
    }
  }

  function extractSchemaKeys(schemaBody: unknown) {
    schemaKeys = [];
    if (!schemaBody || typeof schemaBody !== "object") return;
    const body = schemaBody as { properties?: Record<string, { type?: string; title?: string }> };
    if (body.properties) {
      schemaKeys = Object.entries(body.properties).map(([key, prop]) => ({
        name: key,
        type: prop?.type ?? "string",
        title: prop?.title ?? key,
      }));
    } else {
      schemaKeys = Object.keys(body).map((key) => ({ name: key, type: "string", title: key }));
    }
  }

  function openCreateModal() {
    editingTemplate = null;
    templateName = "";
    templateShortname = "";
    content = defaultContent;
    saveError = "";
    targetSpaceName = querySpaceName || "";
    schemaShortname = "";
    showOptionalFields = isSpaceLocked;
    availableSchemas = [];
    schemaKeys = [];
    loadSpaces();
    if (targetSpaceName) loadSchemasForSpace(targetSpaceName);
    showModal = true;
  }

  function openEditModal(template: TemplateRecord) {
    editingTemplate = template;
    templateName = getTemplateTitle(template);
    templateShortname = template.shortname;
    content = template.attributes?.payload?.body?.content || defaultContent;
    const body = template.attributes?.payload?.body;
    targetSpaceName = body?.space_name || "";
    schemaShortname = body?.schema_shortname || "";
    showOptionalFields = !!(targetSpaceName || schemaShortname);
    schemaKeys = [];
    loadSpaces();
    if (targetSpaceName) loadSchemasForSpace(targetSpaceName);
    saveError = "";
    showModal = true;
  }

  function closeModal() {
    if (isSaving) return;
    showModal = false;
    editingTemplate = null;
  }

  async function handleSave(event: SubmitEvent) {
    event.preventDefault();
    if (!templateName.trim()) {
      saveError = $_("templates.messages.name_required");
      return;
    }
    if (!editingTemplate && !templateShortname.trim()) {
      saveError = $_("templates.messages.shortname_required");
      return;
    }
    if (!content.trim()) {
      saveError = $_("templates.messages.content_required");
      return;
    }
    if ((targetSpaceName.trim() || isSpaceLocked) && !schemaShortname.trim()) {
      saveError = $_("templates.messages.schema_required");
      return;
    }

    isSaving = true;
    saveError = "";
    const data: { title: string; content: string; space_name?: string; schema_shortname?: string } = {
      title: templateName.trim(),
      content: content.trim(),
    };
    if (targetSpaceName.trim()) data.space_name = targetSpaceName.trim();
    if (schemaShortname.trim()) data.schema_shortname = schemaShortname.trim();

    try {
      const success = editingTemplate
        ? await updateTemplates(editingTemplate.shortname, editingTemplate.attributes.space_name ?? APPLICATIONS_SPACE, editingTemplate.subpath, data)
        : await createTemplate(templateShortname.trim(), data);

      if (success) {
        toasts.success(editingTemplate ? $_("templates.messages.updated") : $_("templates.messages.saved"));
        isSaving = false;
        closeModal();
        await loadTemplates();
      } else {
        saveError = editingTemplate ? $_("templates.messages.update_failed") : $_("templates.messages.save_failed");
      }
    } catch (error) {
      log.error("Error saving template:", error);
      saveError = $_("templates.messages.save_error");
    } finally {
      isSaving = false;
    }
  }

  async function handleDelete(template: TemplateRecord) {
    const name = getTemplateTitle(template);
    const deleted = await confirm({
      title: $_("templates.delete_modal.delete_button"),
      body: `${$_("templates.delete_modal.confirm", { values: { name } })} ${$_("templates.delete_modal.warning")}`,
      variant: "danger",
      confirmLabel: $_("templates.delete_modal.delete_button"),
      action: async () => {
        const ok = await deleteTemplate(template.shortname, template.attributes.space_name ?? APPLICATIONS_SPACE, template.subpath);
        if (!ok) throw new Error($_("templates.messages.delete_failed"));
      },
    });
    if (!deleted) return;
    toasts.success($_("templates.messages.deleted"));
    await loadTemplates();
  }

  function getTemplateTitle(template: TemplateRecord): string {
    const parts = template.subpath.split("/");
    return template.attributes?.payload?.body?.title || parts[parts.length - 1] || template.shortname;
  }

  function nameOf(record: NamedRecord): string {
    return localized(record.attributes?.displayname as never, $locale) || record.shortname;
  }

  const inputClass =
    "w-full px-3 py-2 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary disabled:opacity-60";
</script>

<div class="mx-auto max-w-6xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("templates.title")} description={$_("templates.subtitle")} icon={FileLinesOutline}>
    {#snippet actions()}
      <button type="button" class="app-btn app-btn-primary" onclick={openCreateModal}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("templates.create_button")}
      </button>
    {/snippet}
  </PageHeader>

  {#if isLoading && templates.length === 0}
    <LoadingState label={$_("templates.loading")} />
  {:else if loadError}
    <ErrorState title={$_("templates.messages.load_failed")} error={loadError} onRetry={loadTemplates} />
  {:else if templates.length === 0}
    <EmptyState icon={FileLinesOutline} title={$_("templates.empty_title")} hint={$_("templates.empty_subtitle")}>
      <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={openCreateModal}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("templates.empty_create_button")}
      </button>
    </EmptyState>
  {:else}
    <div class="mb-3">
      <Badge>{$_("templates.total_count", { values: { count: templates.length } })}</Badge>
    </div>
    <LoadingState variant="overlay" loading={isLoading}>
      <div class="rounded-card border border-border bg-surface-2 shadow-card overflow-hidden">
        <div class="overflow-x-auto">
          <table class="w-full text-sm text-start border-collapse">
            <thead class="sticky top-0 z-10 bg-surface-3 text-xs text-text-muted">
              <tr>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("templates.table.template")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("fields.space")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("templates.form.schema_label")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("fields.shortname")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("templates.table.owner")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("templates.table.created")}</th>
                <th scope="col" class="px-4 py-3 text-start font-semibold">{$_("templates.table.updated")}</th>
                <th scope="col" class="px-4 py-3 text-end font-semibold">{$_("templates.table.actions")}</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-border">
              {#each templates as template (template.uuid ?? template.shortname)}
                <tr class="hover:bg-surface-3 transition-colors">
                  <td class="px-4 py-3">
                    <div class="font-medium text-text">{getTemplateTitle(template)}</div>
                    <div class="text-xs text-text-faint break-all">{template.subpath}</div>
                  </td>
                  <td class="px-4 py-3"><Badge size="sm">{template.attributes?.space_name || APPLICATIONS_SPACE}</Badge></td>
                  <td class="px-4 py-3">
                    {#if template.attributes?.payload?.body?.schema_shortname}
                      <Badge variant="info" size="sm">{template.attributes.payload.body.schema_shortname}</Badge>
                    {:else}
                      <span class="text-text-faint">—</span>
                    {/if}
                  </td>
                  <td class="px-4 py-3"><code class="text-xs text-text-muted">{template.shortname}</code></td>
                  <td class="px-4 py-3 text-text-muted">{template.attributes.owner_shortname}</td>
                  <td class="px-4 py-3 text-text-muted tabular-nums whitespace-nowrap">{formatDate(template.attributes.created_at, "datetime", $locale)}</td>
                  <td class="px-4 py-3 text-text-muted tabular-nums whitespace-nowrap">{formatDate(template.attributes.updated_at, "datetime", $locale)}</td>
                  <td class="px-4 py-3">
                    <div class="flex items-center justify-end gap-1">
                      <IconButton label="{$_('templates.table.edit')} {getTemplateTitle(template)}" size="sm" onclick={() => openEditModal(template)}>
                        <EditOutline size="sm" />
                      </IconButton>
                      <IconButton label="{$_('templates.table.delete')} {getTemplateTitle(template)}" size="sm" variant="danger" onclick={() => handleDelete(template)}>
                        <TrashBinOutline size="sm" />
                      </IconButton>
                    </div>
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </div>
    </LoadingState>
  {/if}
</div>

{#if showModal}
  <Modal
    title={editingTemplate ? $_("templates.edit_modal.title") : $_("templates.create_modal.title")}
    size="4xl"
    dismissable={!isSaving}
    onClose={closeModal}
  >
    <form id="template-form" class="space-y-5" onsubmit={handleSave}>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label for="template-name" class="block text-sm font-medium text-text mb-1.5">{$_("templates.form.name_label")}</label>
          <input id="template-name" type="text" class={inputClass} bind:value={templateName} placeholder={$_("templates.form.name_placeholder")} disabled={isSaving} required data-autofocus />
        </div>

        <div>
          <label for="template-shortname" class="block text-sm font-medium text-text mb-1.5">{$_("templates.form.shortname_label")}</label>
          <div class="flex gap-2">
            <input
              id="template-shortname"
              type="text"
              class={inputClass}
              bind:value={templateShortname}
              placeholder={$_("templates.form.shortname_placeholder")}
              disabled={isSaving || !!editingTemplate}
              required={!editingTemplate}
            />
            {#if !editingTemplate}
              <button type="button" class="app-btn app-btn-secondary app-btn-sm shrink-0" onclick={() => (templateShortname = "auto")} disabled={isSaving}>
                {$_("buttons.auto")}
              </button>
            {/if}
          </div>
          {#if !editingTemplate}
            <p class="mt-1 text-xs text-text-muted">{$_("create_entry.shortname.help_text")}</p>
          {/if}
        </div>
      </div>

      {#if !isSpaceLocked}
        <button
          type="button"
          class="app-btn app-btn-ghost app-btn-sm"
          aria-expanded={showOptionalFields}
          aria-controls="template-optional-fields"
          onclick={() => (showOptionalFields = !showOptionalFields)}
        >
          <span class="inline-block transition-transform {showOptionalFields ? 'rotate-90' : 'rtl:rotate-180'}" aria-hidden="true">▸</span>
          {$_("templates.form.optional_fields_toggle")}
        </button>
      {/if}

      {#if showOptionalFields || isSpaceLocked}
        <div id="template-optional-fields" class="rounded-card border border-border bg-surface p-4 space-y-4">
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label for="template-target-space" class="block text-sm font-medium text-text mb-1.5">
                {$_("templates.form.target_space_label")}
                {#if !isSpaceLocked}
                  <span class="text-xs text-text-faint font-normal">({$_("common.optional")})</span>
                {/if}
              </label>
              {#if isSpaceLocked}
                <input id="template-target-space" type="text" value={targetSpaceName} disabled class={inputClass} />
              {:else}
                <select id="template-target-space" value={targetSpaceName} onchange={handleTargetSpaceChange} disabled={isSaving || loadingSpaces} class={inputClass}>
                  <option value="">{loadingSpaces ? $_("common.loading") : $_("templates.form.select_space")}</option>
                  {#each availableSpaces as space (space.shortname)}
                    <option value={space.shortname}>{nameOf(space)}</option>
                  {/each}
                </select>
              {/if}
            </div>
            <div>
              <label for="template-schema" class="block text-sm font-medium text-text mb-1.5">
                {$_("templates.form.schema_label")}
                {#if targetSpaceName || isSpaceLocked}
                  <span class="text-danger" aria-hidden="true">*</span>
                {:else}
                  <span class="text-xs text-text-faint font-normal">({$_("common.optional")})</span>
                {/if}
              </label>
              <select
                id="template-schema"
                value={schemaShortname}
                onchange={handleSchemaChange}
                disabled={isSaving || !targetSpaceName || loadingSchemas}
                class={inputClass}
                required={!!targetSpaceName || isSpaceLocked}
              >
                <option value="">
                  {#if loadingSchemas}
                    {$_("common.loading")}
                  {:else if !targetSpaceName}
                    {$_("templates.form.select_space_first")}
                  {:else}
                    {$_("templates.form.select_schema")}
                  {/if}
                </option>
                {#each availableSchemas as schema (schema.shortname)}
                  <option value={schema.shortname}>{nameOf(schema)}</option>
                {/each}
              </select>
            </div>
          </div>

          {#if schemaKeys.length > 0}
            <div>
              <h4 class="text-sm font-medium text-text mb-2">
                {$_("templates.form.schema_keys_title")}
                <span class="text-xs text-text-faint font-normal">{$_("templates.form.schema_keys_hint")}</span>
              </h4>
              <ul class="flex flex-wrap gap-2 list-none p-0 m-0">
                {#each schemaKeys as key (key.name)}
                  <li
                    class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs bg-primary-soft text-primary cursor-grab select-none"
                    draggable={true}
                    ondragstart={(e: DragEvent) => {
                      e.dataTransfer?.setData("application/json", JSON.stringify(key));
                      if (e.dataTransfer) e.dataTransfer.effectAllowed = "copy";
                    }}
                    title="{key.title} ({key.type})"
                  >
                    <span class="font-medium">{key.name}</span>
                    <span class="opacity-70">{key.type}</span>
                  </li>
                {/each}
              </ul>
            </div>
          {/if}
        </div>
      {/if}

      {#if saveError}
        <ErrorState compact message={saveError} />
      {/if}

      <div>
        <MarkdownEditor bind:content />
      </div>

      <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-1 text-sm rounded-card border border-border bg-surface p-4">
        <div class="flex gap-1"><dt class="font-medium text-text">{$_("templates.info.space")}</dt><dd class="text-text-muted">{saveSpace}</dd></div>
        <div class="flex gap-1"><dt class="font-medium text-text">{$_("templates.info.subpath")}</dt><dd class="text-text-muted break-all">templates/{templateShortname || "…"}</dd></div>
        <div class="flex gap-1"><dt class="font-medium text-text">{$_("templates.info.content_type")}</dt><dd class="text-text-muted">Markdown</dd></div>
        <div class="flex gap-1"><dt class="font-medium text-text">{$_("templates.info.resource_type")}</dt><dd class="text-text-muted">{$_("templates._val")}</dd></div>
        {#if schemaShortname}
          <div class="flex gap-1"><dt class="font-medium text-text">{$_("templates.form.schema_label")}:</dt><dd class="text-text-muted">{schemaShortname}</dd></div>
        {/if}
      </dl>
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeModal} disabled={isSaving}>
        {$_("common.cancel")}
      </button>
      <button type="submit" form="template-form" class="app-btn app-btn-primary" disabled={isSaving} aria-busy={isSaving}>
        {#if isSaving}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("common.saving")}
        {:else}
          {editingTemplate ? $_("templates.form.update_button") : $_("templates.form.save_button")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
