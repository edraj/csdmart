<script lang="ts">
  import { onMount } from "svelte";
  import { createSpace, deleteSpace, editSpace, getSpaces, searchInCatalog } from "@/lib/dmart_services";
  import { goto as gotoStore } from "@roxi/routify";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { toasts } from "@/lib/toast";
  import { confirm } from "@/lib/confirm";
  import { MANAGEMENT_SPACE } from "@/lib/constants";
  import MetaForm, { type MetaFormData } from "@/components/forms/MetaForm.svelte";
  import type { LocalizedText } from "@/lib/types";
  import Modal from "@/components/Modal.svelte";
  import DataTable from "@/components/DataTable.svelte";
  import Avatar from "@/components/Avatar.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar, { type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import { CogOutline, EditOutline, LayersOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
  import { DmartScope } from "@edraj/tsdmart";
  import type { Translation } from "@edraj/tsdmart/dmart.model";
  import { encodeSubpath, withBase } from "@/lib/paths";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  interface SpaceRecord {
    shortname: string;
    subpath?: string;
    resource_type?: string;
    attributes?: {
      space_name?: string;
      is_active?: boolean;
      owner_shortname?: string;
      created_at?: string;
      updated_at?: string;
      displayname?: LocalizedText;
      description?: LocalizedText;
      payload?: { body?: { title?: string; content?: string } | string };
    };
  }

  type SortKey = "name" | "created" | "updated" | "owner";
  type StatusFilter = "all" | "active" | "inactive";

  let isLoading = $state(true);
  let spaces = $state<SpaceRecord[]>([]);
  let error = $state<unknown>(null);

  let showCreateModal = $state(false);
  let isCreating = $state(false);
  let createError = $state("");
  let metaContent = $state<MetaFormData>({});
  let validateMetaForm = $state<(() => boolean) | null>(null);

  let showEditModal = $state(false);
  let editingSpace = $state<SpaceRecord | null>(null);
  let editIsActive = $state(true);
  let isEditing = $state(false);
  let editError = $state("");
  let editMetaContent = $state<MetaFormData>({});
  let validateEditMetaForm = $state<(() => boolean) | null>(null);

  let searchQuery = $state("");
  let selectedStatus = $state<StatusFilter>("all");
  let sortBy = $state<SortKey>("name");
  let sortOrder = $state<SortOrder>("asc");
  let searchResults = $state<SpaceRecord[] | null>(null);
  let isSearching = $state(false);

  $effect(() => setTitle($_("route_labels.admin_dashboard_title")));

  const statusOptions = $derived<Array<{ value: StatusFilter; label: string }>>([
    { value: "all", label: $_("admin_dashboard.filters.all") },
    { value: "active", label: $_("admin_dashboard.filters.active") },
    { value: "inactive", label: $_("admin_dashboard.filters.inactive") },
  ]);

  const sortOptions = $derived([
    { value: "name", label: $_("admin_dashboard.sort.name") },
    { value: "created", label: $_("admin_dashboard.sort.created") },
    { value: "updated", label: $_("admin_dashboard.sort.updated") },
    { value: "owner", label: $_("admin_dashboard.sort.owner") },
  ]);

  const indexAttributes = $derived([
    { key: "displayname", name: $_("admin_dashboard.columns.space") },
    { key: "is_active", name: $_("admin_dashboard.columns.status") },
    { key: "owner_shortname", name: $_("admin_dashboard.columns.owner") },
    { key: "created_at", name: $_("admin_dashboard.columns.created") },
  ]);

  onMount(loadSpaces);

  async function loadSpaces() {
    isLoading = true;
    error = null;
    try {
      const response = await getSpaces(false, DmartScope.managed);
      spaces = (response.records ?? []) as unknown as SpaceRecord[];
    } catch (err) {
      log.error("Error fetching spaces:", err);
      error = err;
    } finally {
      isLoading = false;
    }
  }

  async function performSearch(query: string) {
    const q = query.trim();
    if (!q) {
      searchResults = null;
      return;
    }
    isSearching = true;
    try {
      searchResults = (await searchInCatalog(q)) as unknown as SpaceRecord[];
    } catch (err) {
      log.error("Error performing search:", err);
      searchResults = [];
    } finally {
      isSearching = false;
    }
  }

  function getDisplayName(space: SpaceRecord): string {
    const body = space.attributes?.payload?.body;
    return (
      localized(space.attributes?.displayname as never, $locale) ||
      (typeof body === "object" && body?.title) ||
      space.shortname ||
      $_("admin_dashboard.unnamed_space")
    );
  }

  function getDescription(space: SpaceRecord): string {
    const fromAttr = localized(space.attributes?.description as never, $locale);
    if (fromAttr) return cleanHtmlContent(fromAttr);
    const body = space.attributes?.payload?.body;
    if (typeof body === "object" && typeof body?.content === "string") return cleanHtmlContent(body.content);
    if (typeof body === "string") return cleanHtmlContent(body);
    return "";
  }

  function cleanHtmlContent(htmlContent: string): string {
    const tempDiv = document.createElement("div");
    tempDiv.innerHTML = htmlContent;
    const text = (tempDiv.textContent || "").replace(/\s+/g, " ").trim();
    return text.length > 200 ? text.substring(0, 200) + "…" : text;
  }

  function sortValue(space: SpaceRecord): string | number {
    switch (sortBy) {
      case "created":
        return new Date(space.attributes?.created_at || 0).getTime();
      case "updated":
        return new Date(space.attributes?.updated_at || 0).getTime();
      case "owner":
        return (space.attributes?.owner_shortname || "").toLowerCase();
      default:
        return getDisplayName(space).toLowerCase();
    }
  }

  const displayedSpaces = $derived.by(() => {
    const source = searchResults ?? spaces;
    const filtered =
      searchResults || selectedStatus === "all"
        ? [...source]
        : source.filter((s) => (selectedStatus === "active" ? !!s.attributes?.is_active : !s.attributes?.is_active));
    const dir = sortOrder === "asc" ? 1 : -1;
    return filtered.sort((a, b) => {
      const av = sortValue(a);
      const bv = sortValue(b);
      return av < bv ? -dir : av > bv ? dir : 0;
    });
  });

  const activeCount = $derived(spaces.filter((s) => s.attributes?.is_active).length);
  const filtersActive = $derived(!!searchResults || selectedStatus !== "all");

  function clearFilters() {
    searchQuery = "";
    selectedStatus = "all";
    sortBy = "name";
    sortOrder = "asc";
    searchResults = null;
  }

  function hrefFor(record: SpaceRecord): string {
    if (record.resource_type === "space" || !record.resource_type) {
      return withBase(`/dashboard/admin/${encodeURIComponent(record.shortname)}`);
    }
    return withBase(
      `/dashboard/admin/${encodeURIComponent(record.attributes?.space_name ?? "")}/${encodeSubpath(record.subpath)}/${encodeURIComponent(record.shortname)}/${encodeURIComponent(record.resource_type)}`,
    );
  }

  function handleRecordClick(record: SpaceRecord, event?: MouseEvent | KeyboardEvent) {
    event?.preventDefault?.();
    if (record.resource_type === "space" || !record.resource_type) {
      goto("/dashboard/admin/[space_name]", { space_name: record.shortname });
      return;
    }
    // Dash-encoded like every other [subpath] link; the target page decodes
    // dashes, so a percent-encoded "/" would reach the API verbatim.
    goto("/dashboard/admin/[space_name]/[subpath]/[shortname]/[resource_type]", {
      space_name: record.attributes?.space_name ?? "",
      subpath: encodeSubpath(record.subpath),
      shortname: record.shortname,
      resource_type: record.resource_type,
    });
  }

  function openCreateModal() {
    metaContent = {};
    createError = "";
    showCreateModal = true;
  }

  function closeCreateModal() {
    if (isCreating) return;
    showCreateModal = false;
    createError = "";
  }

  function openEditModal(space: SpaceRecord) {
    editingSpace = space;
    editIsActive = space.attributes?.is_active ?? true;
    editMetaContent = {
      shortname: space.shortname,
      displayname: space.attributes?.displayname || { en: getDisplayName(space) },
      description: space.attributes?.description || { en: getDescription(space) },
    };
    editError = "";
    showEditModal = true;
  }

  function closeEditModal() {
    if (isEditing) return;
    showEditModal = false;
    editingSpace = null;
    editError = "";
  }

  async function handleCreateSpace(event: SubmitEvent) {
    event.preventDefault();
    if (!validateMetaForm?.()) {
      createError = $_("admin_dashboard.messages.fill_required");
      return;
    }
    isCreating = true;
    createError = "";
    try {
      const { shortname, displayname, description } = metaContent as {
        shortname: string;
        displayname: Translation;
        description: Translation;
      };
      const create = await createSpace({ shortname, displayname, description });
      if (create === undefined) {
        createError = $_("admin_dashboard.messages.invalid_shortname");
        return;
      }
      isCreating = false;
      closeCreateModal();
      toasts.success($_("admin_dashboard.messages.created", { values: { name: shortname } }));
      await loadSpaces();
    } catch (err) {
      log.error("Error creating space:", err);
      createError = $_("admin_dashboard.messages.create_failed");
    } finally {
      isCreating = false;
    }
  }

  async function handleEditSpace(event: SubmitEvent) {
    event.preventDefault();
    if (!editingSpace) return;
    if (!validateEditMetaForm?.()) {
      editError = $_("admin_dashboard.messages.fill_required");
      return;
    }
    isEditing = true;
    editError = "";
    try {
      const { displayname, description } = editMetaContent;
      await editSpace(editingSpace.shortname, { is_active: editIsActive, displayname, description });
      isEditing = false;
      closeEditModal();
      toasts.success($_("admin_dashboard.messages.updated"));
      await loadSpaces();
    } catch (err) {
      log.error("Error editing space:", err);
      editError = $_("admin_dashboard.messages.update_failed");
    } finally {
      isEditing = false;
    }
  }

  async function handleDeleteSpace(space: SpaceRecord) {
    const deleted = await confirm({
      title: $_("admin_dashboard.modal.delete.title"),
      body: `${$_("admin_dashboard.modal.delete.space_label")}: ${getDisplayName(space)} (${space.shortname})\n\n${$_("admin_dashboard.modal.delete.warning")}`,
      variant: "danger",
      confirmLabel: $_("admin_dashboard.modal.delete.button"),
      action: () => deleteSpace(space.shortname),
    });
    if (!deleted) return;
    toasts.success($_("admin_dashboard.messages.deleted", { values: { name: space.shortname } }));
    await loadSpaces();
  }
</script>

<div class="mx-auto max-w-6xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={$_("route_labels.admin_dashboard_title")}
    description={$_("route_labels.admin_dashboard_welcome")}
    icon={LayersOutline}
  >
    {#snippet actions()}
      <a href={withBase("/dashboard/admin/settings")} class="app-btn app-btn-secondary">
        <CogOutline size="sm" aria-hidden="true" />
        {$_("admin_settings.title")}
      </a>
      <button type="button" class="app-btn app-btn-primary" onclick={openCreateModal}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("admin_dashboard.modal.create.button")}
      </button>
    {/snippet}
  </PageHeader>

  {#if isLoading}
    <LoadingState label={$_("loading.spaces")} />
  {:else if error}
    <ErrorState title={$_("admin_dashboard.error.title")} {error} onRetry={loadSpaces} />
  {:else if spaces.length === 0}
    <EmptyState icon={LayersOutline} title={$_("admin_dashboard.empty.title")} hint={$_("admin_dashboard.empty.description")}>
      <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={openCreateModal}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("admin_dashboard.actions.create_first")}
      </button>
    </EmptyState>
  {:else}
    <div class="flex flex-wrap items-center gap-2 mb-4" role="group" aria-label={$_("statistics")}>
      <Badge>{$_("admin_dashboard.stats.total", { values: { count: spaces.length } })}</Badge>
      <Badge variant="success">{$_("admin_dashboard.stats.active", { values: { count: activeCount } })}</Badge>
    </div>

    <CatalogToolbar
      class="mb-4"
      bind:search={searchQuery}
      placeholder={$_("route_labels.placeholder_search_by_name_desc")}
      onSearch={performSearch}
      onClear={() => (searchResults = null)}
      bind:sort={sortBy}
      {sortOptions}
      bind:order={sortOrder}
    >
      {#snippet filters()}
        <label for="status-filter" class="sr-only">{$_("catalog_contents.filters.status")}</label>
        <select
          id="status-filter"
          bind:value={selectedStatus}
          class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary"
        >
          {#each statusOptions as option (option.value)}
            <option value={option.value}>{option.label}</option>
          {/each}
        </select>
        {#if filtersActive}
          <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={clearFilters}>
            {$_("search_filters.clear_filters")}
          </button>
        {/if}
      {/snippet}
    </CatalogToolbar>

    {#if filtersActive}
      <p class="text-sm text-text-muted mb-3" aria-live="polite">
        {#if searchResults}
          {$_("admin_dashboard.search_results", { values: { count: displayedSpaces.length, query: searchQuery } })}
        {:else}
          {$_("search_filters.results_count", { values: { displayed: displayedSpaces.length, total: spaces.length } })}
        {/if}
      </p>
    {/if}

    <DataTable
      items={displayedSpaces}
      {indexAttributes}
      loading={isSearching}
      rowHref={hrefFor}
      rowLabel={getDisplayName}
      onRowClick={handleRecordClick}
      totalItems={displayedSpaces.length}
      itemsPerPage={displayedSpaces.length || 1}
      emptyMessage={searchResults ? $_("search_filters.no_results.description") : undefined}
    >
      {#snippet cell({ item, attr })}
        {#if attr.key === "displayname"}
          <div class="flex items-center gap-3 min-w-0">
            <Avatar alt={item.shortname} size="32" />
            <div class="min-w-0">
              <div class="font-medium text-text truncate">{getDisplayName(item)}</div>
              {#if getDescription(item)}
                <div class="text-xs text-text-faint truncate max-w-xs">{getDescription(item)}</div>
              {/if}
            </div>
          </div>
        {:else if attr.key === "is_active"}
          <Badge variant={item.attributes?.is_active ? "success" : "danger"} size="sm">
            {item.attributes?.is_active ? $_("admin_dashboard.filters.active") : $_("admin_dashboard.filters.inactive")}
          </Badge>
        {:else if attr.key === "owner_shortname"}
          <span class="text-text-muted">{item.attributes?.owner_shortname || $_("common.unknown")}</span>
        {:else if attr.key === "created_at"}
          <span class="text-text-muted whitespace-nowrap">{formatDate(item.attributes?.created_at, "date", $locale)}</span>
        {/if}
      {/snippet}

      {#snippet actions({ item })}
        {#if item.resource_type === "space" || !item.resource_type}
          <IconButton label="{$_('admin_dashboard.actions.manage')} {getDisplayName(item)}" size="sm" href={hrefFor(item)}>
            <CogOutline size="sm" />
          </IconButton>
          <IconButton label="{$_('admin_dashboard.actions.edit')} {getDisplayName(item)}" size="sm" onclick={() => openEditModal(item)}>
            <EditOutline size="sm" />
          </IconButton>
          {#if item.shortname !== MANAGEMENT_SPACE}
            <IconButton label="{$_('admin_dashboard.modal.delete.button')} {getDisplayName(item)}" size="sm" variant="danger" onclick={() => handleDeleteSpace(item)}>
              <TrashBinOutline size="sm" />
            </IconButton>
          {/if}
        {:else}
          <Badge size="sm">{item.resource_type}</Badge>
        {/if}
      {/snippet}
    </DataTable>
  {/if}
</div>

{#if showCreateModal}
  <Modal onClose={closeCreateModal} title={$_("admin_dashboard.modal.create.title")} size="lg" dismissable={!isCreating}>
    {#snippet icon()}
      <PlusOutline size="lg" />
    {/snippet}

    <p class="text-sm text-text-muted mb-4">{$_("admin_dashboard.modal.create.description")}</p>
    <form id="create-space-form" onsubmit={handleCreateSpace}>
      <MetaForm bind:formData={metaContent} bind:validateFn={validateMetaForm} isCreate={true} fullWidth={true} />
    </form>
    {#if createError}
      <ErrorState compact message={createError} class="mt-4" />
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeCreateModal} disabled={isCreating}>
        {$_("admin_dashboard.modal.cancel")}
      </button>
      <button type="submit" form="create-space-form" class="app-btn app-btn-primary" disabled={isCreating || !metaContent.shortname} aria-busy={isCreating}>
        {#if isCreating}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("admin_dashboard.modal.creating")}
        {:else}
          {$_("admin_dashboard.modal.create.button")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

{#if showEditModal && editingSpace}
  <Modal onClose={closeEditModal} title={$_("admin_dashboard.modal.edit.title")} size="lg" dismissable={!isEditing}>
    {#snippet icon()}
      <EditOutline size="lg" />
    {/snippet}

    <p class="text-sm text-text-muted mb-4">{$_("admin_dashboard.modal.edit.description")}</p>
    <form id="edit-space-form" onsubmit={handleEditSpace} class="space-y-4">
      <MetaForm bind:formData={editMetaContent} bind:validateFn={validateEditMetaForm} isCreate={false} fullWidth={true} />

      <label class="flex items-center gap-2 p-3 rounded-control border border-border bg-surface text-sm text-text cursor-pointer">
        <input type="checkbox" class="accent-primary" bind:checked={editIsActive} />
        {$_("admin_dashboard.modal.edit.space_active")}
      </label>
    </form>
    {#if editError}
      <ErrorState compact message={editError} class="mt-4" />
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeEditModal} disabled={isEditing}>
        {$_("admin_dashboard.modal.cancel")}
      </button>
      <button type="submit" form="edit-space-form" class="app-btn app-btn-primary" disabled={isEditing} aria-busy={isEditing}>
        {#if isEditing}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("admin_dashboard.modal.updating")}
        {:else}
          {$_("admin_dashboard.modal.edit.button")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
