<script lang="ts">
  import { onMount } from "svelte";
  import { goto as gotoStore, params } from "@roxi/routify";
  import { deleteEntity, editSpace, getSpaces, getSpaceHideFolders, buildHideFoldersSearch, mergeSearch } from "@/lib/dmart_services";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { toasts } from "@/lib/toast";
  import { encodeSubpath, withBase } from "@/lib/paths";
  import { Dmart, RequestType, DmartScope, ResourceType, QueryType, SortType } from "@edraj/tsdmart";
  import FolderForm from "@/components/forms/FolderForm.svelte";
  import MetaForm, { type MetaFormData } from "@/components/forms/MetaForm.svelte";
  import { applyFolderContentDefaults } from "@/lib/folder_defaults";
  import { recordsOf, type LocalizedText, type SpaceAttributes } from "@/lib/types";
  import Modal from "@/components/Modal.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar, { type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import Card from "@/components/ui/Card.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import {
    CogOutline,
    EditOutline,
    FileLinesOutline,
    FolderOutline,
    FolderPlusOutline,
    ImageOutline,
    TrashBinOutline,
    UserOutline,
  } from "flowbite-svelte-icons";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  interface ContentRecord {
    shortname: string;
    subpath: string;
    resource_type: string;
    attributes?: {
      is_active?: boolean;
      owner_shortname?: string;
      created_at?: string;
      updated_at?: string;
      displayname?: LocalizedText;
      description?: LocalizedText;
      payload?: { body?: Record<string, unknown> };
    };
  }

  type SortKey = "name" | "created" | "updated" | "owner";

  let isLoading = $state(false);
  let allContents = $state<ContentRecord[]>([]);
  let error = $state<unknown>(null);
  let spaceName = $state("");
  let spaceDisplayName = $state("");
  let spaceHideFolders = $state<string[]>([]);
  let isEditMode = $state(false);

  // Search and filter state
  let searchQuery = $state("");
  let selectedType = $state("all");
  let selectedStatus = $state("all");
  let sortBy = $state<SortKey>("name");
  let sortOrder = $state<SortOrder>("asc");

  $effect(() => setTitle(spaceDisplayName || spaceName, $_("route_labels.admin_dashboard_title")));

  const typeOptions = $derived([
    { value: "all", label: $_("admin_dashboard.filters.all") },
    { value: "folder", label: $_("admin_dashboard.filters.folder") },
    { value: "content", label: $_("admin_dashboard.filters.content") },
    { value: "post", label: $_("admin_dashboard.filters.post") },
    { value: "ticket", label: $_("admin_dashboard.filters.ticket") },
    { value: "user", label: $_("admin_dashboard.filters.user") },
    { value: "media", label: $_("admin_dashboard.filters.media") },
  ]);

  const statusOptions = $derived([
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

  // Folder modal state. A new folder's listing settings: the FolderForm
  // defaults, sorted newest first.
  const emptyFolder = () =>
    applyFolderContentDefaults({
      title: "",
      content: "",
      is_active: true,
      tags: [] as string[],
      sort_by: "created_at",
      sort_type: "descending",
    });
  let showFolderModal = $state(false);
  let folderContent = $state(emptyFolder());
  let isSavingFolder = $state(false);
  let metaContent = $state<MetaFormData>({});
  let validateMetaForm = $state<(() => boolean) | null>(null);

  // Space settings modal state
  let showSpaceConfigModal = $state(false);
  let isLoadingSpaceConfig = $state(false);
  let isSavingSpaceConfig = $state(false);
  let spaceConfigError = $state("");
  type SpaceConfigForm = {
    is_active: boolean;
    displayname: { en: string; ar: string; ku: string };
    description: { en: string; ar: string; ku: string };
    slug: string | null;
    ordinal: number;
    icon: string;
    root_registration_signature: string;
    primary_website: string;
    indexing_enabled: boolean;
    capture_misses: boolean;
    check_health: boolean;
    hide_space: boolean;
    tags: string;
    languages: string;
    mirrors: string;
    hide_folders: string;
    active_plugins: string;
  };
  const emptySpaceConfig = (): SpaceConfigForm => ({
    is_active: true,
    displayname: { en: "", ar: "", ku: "" },
    description: { en: "", ar: "", ku: "" },
    slug: null,
    ordinal: 0,
    icon: "",
    root_registration_signature: "",
    primary_website: "",
    indexing_enabled: true,
    capture_misses: false,
    check_health: false,
    hide_space: false,
    tags: "",
    languages: "",
    mirrors: "",
    hide_folders: "",
    active_plugins: "",
  });
  let spaceConfig = $state<SpaceConfigForm>(emptySpaceConfig());

  const flagFields: Array<{ key: "is_active" | "hide_space" | "indexing_enabled" | "capture_misses" | "check_health"; label: () => string }> = [
    { key: "is_active", label: () => $_("admin_space.config.fields.active") },
    { key: "hide_space", label: () => $_("admin_space.config.fields.hide_space") },
    { key: "indexing_enabled", label: () => $_("admin_space.config.fields.indexing_enabled") },
    { key: "capture_misses", label: () => $_("admin_space.config.fields.capture_misses") },
    { key: "check_health", label: () => $_("admin_space.config.fields.check_health") },
  ];
  const listFields: Array<{ key: "tags" | "languages" | "mirrors" | "hide_folders" | "active_plugins"; label: () => string; hint?: () => string }> = [
    { key: "tags", label: () => $_("admin_space.config.fields.tags") },
    { key: "languages", label: () => $_("admin_space.config.fields.languages"), hint: () => $_("admin_space.config.languages_hint") },
    { key: "mirrors", label: () => $_("admin_space.config.fields.mirrors") },
    { key: "hide_folders", label: () => $_("admin_space.config.fields.hide_folders"), hint: () => $_("admin_space.config.hide_folders_hint") },
    { key: "active_plugins", label: () => $_("admin_space.config.fields.active_plugins") },
  ];
  const languageFields: Array<{ key: "en" | "ar" | "ku"; label: () => string }> = [
    { key: "en", label: () => $_("english") },
    { key: "ar", label: () => $_("arabic") },
    { key: "ku", label: () => $_("kurdish") },
  ];

  function stringToArray(value: string): string[] {
    return value.split(",").map((v) => v.trim()).filter((v) => v.length > 0);
  }

  function arrayToString(value: unknown): string {
    return Array.isArray(value) ? value.join(", ") : "";
  }

  function nullIfEmpty(value: string): string | null {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  }

  async function openSpaceConfigModal() {
    spaceConfigError = "";
    isLoadingSpaceConfig = true;
    showSpaceConfigModal = true;
    try {
      const response = await getSpaces(true, DmartScope.managed);
      const match = recordsOf<SpaceAttributes>(response).find((record) => record.shortname === spaceName);
      const attrs: SpaceAttributes = match?.attributes ?? {};
      spaceConfig = {
        is_active: attrs.is_active ?? true,
        displayname: { en: attrs.displayname?.en ?? "", ar: attrs.displayname?.ar ?? "", ku: attrs.displayname?.ku ?? "" },
        description: { en: attrs.description?.en ?? "", ar: attrs.description?.ar ?? "", ku: attrs.description?.ku ?? "" },
        slug: attrs.slug ?? null,
        ordinal: typeof attrs.ordinal === "number" && Number.isFinite(attrs.ordinal) ? attrs.ordinal : 0,
        icon: attrs.icon ?? "",
        root_registration_signature: attrs.root_registration_signature ?? "",
        primary_website: attrs.primary_website ?? "",
        indexing_enabled: attrs.indexing_enabled ?? true,
        capture_misses: attrs.capture_misses ?? false,
        check_health: attrs.check_health ?? false,
        hide_space: attrs.hide_space ?? false,
        tags: arrayToString(attrs.tags),
        languages: arrayToString(attrs.languages),
        mirrors: arrayToString(attrs.mirrors),
        hide_folders: arrayToString(attrs.hide_folders),
        active_plugins: arrayToString(attrs.active_plugins),
      };
    } catch (err) {
      log.error("Error loading space config:", err);
      spaceConfigError = $_("admin_space.config.load_failed");
      spaceConfig = emptySpaceConfig();
    } finally {
      isLoadingSpaceConfig = false;
    }
  }

  function closeSpaceConfigModal() {
    if (isSavingSpaceConfig) return;
    showSpaceConfigModal = false;
    spaceConfigError = "";
  }

  async function handleSaveSpaceConfig(event: SubmitEvent) {
    event.preventDefault();
    spaceConfigError = "";
    isSavingSpaceConfig = true;
    try {
      await editSpace(spaceName, {
        is_active: spaceConfig.is_active,
        displayname: {
          en: nullIfEmpty(spaceConfig.displayname.en),
          ar: nullIfEmpty(spaceConfig.displayname.ar),
          ku: nullIfEmpty(spaceConfig.displayname.ku),
        },
        description: {
          en: nullIfEmpty(spaceConfig.description.en),
          ar: nullIfEmpty(spaceConfig.description.ar),
          ku: nullIfEmpty(spaceConfig.description.ku),
        },
        tags: stringToArray(spaceConfig.tags),
        root_registration_signature: spaceConfig.root_registration_signature,
        primary_website: spaceConfig.primary_website,
        indexing_enabled: spaceConfig.indexing_enabled,
        capture_misses: spaceConfig.capture_misses,
        check_health: spaceConfig.check_health,
        languages: stringToArray(spaceConfig.languages),
        icon: spaceConfig.icon,
        mirrors: stringToArray(spaceConfig.mirrors),
        hide_folders: stringToArray(spaceConfig.hide_folders),
        hide_space: spaceConfig.hide_space,
        active_plugins: stringToArray(spaceConfig.active_plugins),
        ordinal: Number(spaceConfig.ordinal) || 0,
        slug: spaceConfig.slug,
      });
      spaceHideFolders = stringToArray(spaceConfig.hide_folders);
      spaceDisplayName = localized(spaceConfig.displayname, $locale) || spaceName;
      toasts.success($_("admin_space.config.save_success"));
      isSavingSpaceConfig = false;
      closeSpaceConfigModal();
      await loadContents();
    } catch (err) {
      log.error("Error saving space config:", err);
      spaceConfigError = $_("admin_space.config.save_error");
    } finally {
      isSavingSpaceConfig = false;
    }
  }

  onMount(async () => {
    spaceName = $params.space_name;
    // The list does not wait for the (cached) spaces lookup: the first page
    // loads at once and the hidden folders are removed as soon as they are known.
    const spacesPromise = getSpaces(true, DmartScope.managed).catch(() => null);
    const hidePromise = getSpaceHideFolders(spaceName, DmartScope.managed);
    await loadContents();
    spaceHideFolders = await hidePromise;
    if (spaceHideFolders.length > 0) {
      allContents = allContents.filter((item) => !spaceHideFolders.includes(item.shortname));
    }
    const spacesResponse = await spacesPromise;
    const match = spacesResponse?.records.find((r) => r.shortname === spaceName);
    spaceDisplayName = localized((match?.attributes as { displayname?: unknown } | undefined)?.displayname as never, $locale) || spaceName;
  });

  async function loadContents() {
    isLoading = true;
    error = null;
    try {
      const response = await Dmart.query(
        {
          type: QueryType.search,
          space_name: spaceName,
          subpath: "/",
          search: mergeSearch(searchQuery, buildHideFoldersSearch(spaceHideFolders)),
          limit: 100,
          sort_by: "shortname",
          sort_type: SortType.ascending,
          offset: 0,
          retrieve_json_payload: true,
          retrieve_attachments: false,
          exact_subpath: true,
        },
        DmartScope.managed,
      );
      allContents = ((response?.records ?? []) as unknown as ContentRecord[]).filter(
        (item) => !spaceHideFolders.includes(item.shortname),
      );
    } catch (err) {
      log.error("Error fetching space contents:", err);
      error = err;
    } finally {
      isLoading = false;
    }
  }

  function getDisplayName(item: ContentRecord): string {
    return localized(item.attributes?.displayname as never, $locale) || item.shortname;
  }

  function getDescription(item: ContentRecord): string {
    return localized(item.attributes?.description as never, $locale);
  }

  function sortValue(item: ContentRecord): string | number {
    switch (sortBy) {
      case "created":
        return new Date(item.attributes?.created_at || 0).getTime();
      case "updated":
        return new Date(item.attributes?.updated_at || 0).getTime();
      case "owner":
        return (item.attributes?.owner_shortname || "").toLowerCase();
      default:
        return getDisplayName(item).toLowerCase();
    }
  }

  const displayedContents = $derived.by(() => {
    let filtered = allContents;
    if (selectedType !== "all") filtered = filtered.filter((item) => item.resource_type === selectedType);
    if (selectedStatus !== "all") {
      filtered = filtered.filter((item) => (selectedStatus === "active" ? !!item.attributes?.is_active : !item.attributes?.is_active));
    }
    const dir = sortOrder === "asc" ? 1 : -1;
    return [...filtered].sort((a, b) => {
      const av = sortValue(a);
      const bv = sortValue(b);
      return av < bv ? -dir : av > bv ? dir : 0;
    });
  });

  const filtersActive = $derived(searchQuery.trim() !== "" || selectedType !== "all" || selectedStatus !== "all");

  function clearFilters() {
    const hadSearch = searchQuery.trim() !== "";
    searchQuery = "";
    selectedType = "all";
    selectedStatus = "all";
    sortBy = "name";
    sortOrder = "asc";
    if (hadSearch) loadContents();
  }

  function isNavigable(item: ContentRecord): boolean {
    return item.resource_type === "folder" || item.subpath !== "/";
  }

  function subpathOf(item: ContentRecord): string {
    return item.subpath === "/" ? item.shortname : `${item.subpath}/${item.shortname}`;
  }

  function hrefFor(item: ContentRecord): string | undefined {
    if (!isNavigable(item)) return undefined;
    return withBase(`/dashboard/admin/${encodeURIComponent(spaceName)}/${encodeSubpath(subpathOf(item))}`);
  }

  function handleItemClick(item: ContentRecord, event: MouseEvent) {
    if (!isNavigable(item)) return;
    event.preventDefault();
    goto("/dashboard/admin/[space_name]/[subpath]", { space_name: spaceName, subpath: encodeSubpath(subpathOf(item)) });
  }

  function iconFor(type: string) {
    switch (type) {
      case "folder":
        return FolderOutline;
      case "user":
        return UserOutline;
      case "media":
        return ImageOutline;
      default:
        return FileLinesOutline;
    }
  }

  function handleCreateFolder() {
    isEditMode = false;
    metaContent = {};
    folderContent = emptyFolder();
    showFolderModal = true;
  }

  function handleEditFolder(item: ContentRecord) {
    isEditMode = true;
    metaContent = {
      shortname: item.shortname,
      displayname: item.attributes?.displayname || {},
      description: item.attributes?.description || {},
    };
    const existing = item.attributes?.payload?.body ?? {};
    folderContent = applyFolderContentDefaults({ ...emptyFolder(), ...existing });
    showFolderModal = true;
  }

  function closeFolderModal() {
    if (isSavingFolder) return;
    showFolderModal = false;
  }

  async function handleSaveFolder(event: SubmitEvent) {
    event.preventDefault();
    if (validateMetaForm && !validateMetaForm()) return;
    isSavingFolder = true;
    try {
      const response = await Dmart.request({
        space_name: spaceName,
        request_type: isEditMode ? RequestType.update : RequestType.create,
        records: [
          {
            resource_type: ResourceType.folder,
            shortname: (metaContent.shortname as string) || "auto",
            subpath: "/",
            attributes: {
              displayname: metaContent.displayname,
              description: metaContent.description,
              payload: { body: $state.snapshot(folderContent), content_type: "json" },
              is_active: true,
            },
          },
        ],
      });
      if (!response) throw new Error("empty response");
      isSavingFolder = false;
      showFolderModal = false;
      toasts.success(isEditMode ? $_("toast.folder_updated") : $_("toast.folder_created"));
      await loadContents();
    } catch (err) {
      log.error(`Error ${isEditMode ? "updating" : "creating"} folder:`, err);
      toasts.error(isEditMode ? $_("toast.folder_update_failed") : $_("toast.folder_create_failed"));
    } finally {
      isSavingFolder = false;
    }
  }

  // Delete confirmation
  let showDeleteDialog = $state(false);
  let itemToDelete = $state<ContentRecord | null>(null);
  let forceDelete = $state(false);

  function openDeleteDialog(item: ContentRecord) {
    itemToDelete = item;
    forceDelete = false;
    showDeleteDialog = true;
  }

  async function performDelete() {
    if (!itemToDelete) return;
    const ok = await deleteEntity(itemToDelete.shortname, spaceName, "/", itemToDelete.resource_type as ResourceType, forceDelete);
    if (!ok) throw new Error($_("toast.item_delete_failed"));
  }

  async function afterDelete() {
    toasts.success($_("toast.item_deleted"));
    itemToDelete = null;
    await loadContents();
  }
</script>

<div class="mx-auto max-w-6xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader
    title={spaceDisplayName || spaceName}
    description={$_("admin_space.subtitle", { values: { name: spaceName } })}
    icon={FolderOutline}
    backHref="/dashboard/admin"
    backLabel={$_("admin_space.navigation.go_back")}
  >
    {#snippet actions()}
      <IconButton label={$_("admin_space.config.title")} variant="outline" onclick={openSpaceConfigModal}>
        <CogOutline size="sm" />
      </IconButton>
      <button type="button" class="app-btn app-btn-primary" onclick={handleCreateFolder}>
        <FolderPlusOutline size="sm" aria-hidden="true" />
        {$_("admin_space.create_folder")}
      </button>
    {/snippet}
  </PageHeader>

  {#if isLoading && allContents.length === 0}
    <LoadingState />
  {:else if error}
    <ErrorState title={$_("admin_space.error.title")} {error} onRetry={loadContents} />
  {:else if allContents.length === 0 && !filtersActive}
    <EmptyState icon={FolderOutline} title={$_("admin_space.empty.title")} hint={$_("admin_space.empty.description")}>
      <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={handleCreateFolder}>
        <FolderPlusOutline size="sm" aria-hidden="true" />
        {$_("admin_space.create_folder")}
      </button>
    </EmptyState>
  {:else}
    <CatalogToolbar
      class="mb-6"
      bind:search={searchQuery}
      placeholder={$_("route_labels.placeholder_search_by_name_desc")}
      onSearch={() => loadContents()}
      bind:sort={sortBy}
      {sortOptions}
      bind:order={sortOrder}
    >
      {#snippet filters()}
        <label for="type-filter" class="sr-only">{$_("ui.resource_type")}</label>
        <select id="type-filter" bind:value={selectedType} class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary">
          {#each typeOptions as option (option.value)}
            <option value={option.value}>{option.label}</option>
          {/each}
        </select>
        <label for="status-filter" class="sr-only">{$_("catalog_contents.filters.status")}</label>
        <select id="status-filter" bind:value={selectedStatus} class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary">
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

    {#if displayedContents.length === 0}
      <EmptyState title={$_("search_filters.no_results.title")} hint={$_("search_filters.no_results.description")}>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearFilters}>
          {$_("search_filters.clear_filters")}
        </button>
      </EmptyState>
    {:else}
      <LoadingState variant="overlay" loading={isLoading}>
        <ul class="grid gap-4 md:grid-cols-2 lg:grid-cols-3 list-none p-0 m-0">
          {#each displayedContents as item (`${item.subpath}/${item.shortname}`)}
            {@const Icon = iconFor(item.resource_type)}
            {@const href = hrefFor(item)}
            <li class="relative flex">
              <Card
                class="w-full flex flex-col"
                {href}
                onclick={href ? (e: MouseEvent) => handleItemClick(item, e) : undefined}
              >
                <div class="flex items-start gap-3 mb-3 {item.resource_type === 'folder' ? 'pe-20' : ''}">
                  <span class="w-10 h-10 rounded-control bg-primary-soft text-primary flex items-center justify-center shrink-0" aria-hidden="true">
                    <Icon size="md" />
                  </span>
                  <div class="min-w-0 flex-1">
                    <h3 class="text-base font-semibold text-text truncate">{getDisplayName(item)}</h3>
                    <p class="text-sm text-text-muted mt-0.5 line-clamp-2 min-h-10">
                      {getDescription(item) || $_("admin_space.no_description")}
                    </p>
                  </div>
                </div>
                <div class="mt-auto pt-3 border-t border-border flex items-center justify-between gap-2">
                  <div class="flex items-center gap-2">
                    <Badge variant={item.attributes?.is_active ? "success" : "danger"} size="sm">
                      {item.attributes?.is_active ? $_("status.active") : $_("status.inactive")}
                    </Badge>
                    <Badge size="sm">{item.resource_type}</Badge>
                  </div>
                  {#if item.attributes?.created_at}
                    <span class="text-xs text-text-faint tabular-nums">{formatDate(item.attributes.created_at, "date", $locale)}</span>
                  {/if}
                </div>
              </Card>
              {#if item.resource_type === "folder"}
                <!-- Siblings of the card link, never nested inside it. -->
                <div class="absolute top-3 end-3 flex gap-1">
                  <IconButton label="{$_('admin_space.edit_folder')} {getDisplayName(item)}" size="sm" variant="outline" onclick={() => handleEditFolder(item)}>
                    <EditOutline size="sm" />
                  </IconButton>
                  <IconButton label="{$_('admin_space.delete_folder')} {getDisplayName(item)}" size="sm" variant="danger" onclick={() => openDeleteDialog(item)}>
                    <TrashBinOutline size="sm" />
                  </IconButton>
                </div>
              {/if}
            </li>
          {/each}
        </ul>
      </LoadingState>
    {/if}
  {/if}
</div>

{#if showFolderModal}
  <Modal
    title={isEditMode ? $_("admin_space.modal.edit.title") : $_("admin_space.modal.create.title")}
    size="3xl"
    dismissable={!isSavingFolder}
    onClose={closeFolderModal}
  >
    <p class="text-sm text-text-muted mb-5">
      {isEditMode ? $_("admin_space.modal.edit.subtitle") : $_("admin_space.modal.create.subtitle")}
    </p>

    <form id="folder-form" onsubmit={handleSaveFolder} class="space-y-6">
      <section>
        <h4 class="text-base font-semibold text-text">{$_("admin_space.modal.basic_info.title")}</h4>
        <p class="text-sm text-text-muted mb-3">{$_("admin_space.modal.basic_info.description")}</p>
        <MetaForm bind:formData={metaContent} bind:validateFn={validateMetaForm} isCreate={!isEditMode} fullWidth={true} />
      </section>

      <section>
        <h4 class="text-base font-semibold text-text">{$_("admin_space.modal.folder_config.title")}</h4>
        <p class="text-sm text-text-muted mb-3">{$_("admin_space.modal.folder_config.description")}</p>
        <FolderForm bind:content={folderContent} space_name={spaceName} fullWidth={true} />
      </section>
    </form>

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeFolderModal} disabled={isSavingFolder}>
        {$_("admin_space.modal.cancel")}
      </button>
      <button type="submit" form="folder-form" class="app-btn app-btn-primary" disabled={isSavingFolder} aria-busy={isSavingFolder}>
        {#if isSavingFolder}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {isEditMode ? $_("admin_space.modal.updating") : $_("admin_space.modal.creating")}
        {:else}
          {isEditMode ? $_("admin_space.modal.updatebtn") : $_("admin_space.modal.createbtn")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

{#if showSpaceConfigModal}
  <Modal title={$_("admin_space.config.title")} size="2xl" dismissable={!isSavingSpaceConfig} onClose={closeSpaceConfigModal}>
    {#snippet icon()}
      <CogOutline size="lg" />
    {/snippet}
    <p class="text-sm text-text-muted mb-4">{$_("admin_space.config.subtitle", { values: { name: spaceName } })}</p>

    {#if isLoadingSpaceConfig}
      <LoadingState />
    {:else}
      {#if spaceConfigError}
        <ErrorState compact message={spaceConfigError} class="mb-4" />
      {/if}

      <form id="space-config-form" class="space-y-5" onsubmit={handleSaveSpaceConfig}>
        <fieldset class="border-0 p-0 m-0 min-w-0">
          <legend class="text-xs font-medium text-text-muted mb-2">{$_("admin_space.config.flags")}</legend>
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
            {#each flagFields as field (field.key)}
              <label class="flex items-center gap-2 text-sm text-text cursor-pointer">
                <input type="checkbox" class="accent-primary" bind:checked={spaceConfig[field.key]} />
                {field.label()}
              </label>
            {/each}
          </div>
        </fieldset>

        <fieldset class="border-0 p-0 m-0 min-w-0">
          <legend class="text-xs font-medium text-text-muted mb-2">{$_("fields.displayname")}</legend>
          <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
            {#each languageFields as lang (lang.key)}
              <div>
                <label for="space-cfg-dn-{lang.key}" class="sr-only">{$_("fields.displayname")} ({lang.label()})</label>
                <input id="space-cfg-dn-{lang.key}" type="text" placeholder={lang.label()} bind:value={spaceConfig.displayname[lang.key]} class="config-input" />
              </div>
            {/each}
          </div>
        </fieldset>

        <fieldset class="border-0 p-0 m-0 min-w-0">
          <legend class="text-xs font-medium text-text-muted mb-2">{$_("fields.description")}</legend>
          <div class="grid grid-cols-1 sm:grid-cols-3 gap-2">
            {#each languageFields as lang (lang.key)}
              <div>
                <label for="space-cfg-desc-{lang.key}" class="sr-only">{$_("fields.description")} ({lang.label()})</label>
                <textarea id="space-cfg-desc-{lang.key}" rows="2" placeholder={lang.label()} bind:value={spaceConfig.description[lang.key]} class="config-input"></textarea>
              </div>
            {/each}
          </div>
        </fieldset>

        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label for="space-cfg-slug" class="config-label">{$_("fields.slug")}</label>
            <input id="space-cfg-slug" type="text" bind:value={spaceConfig.slug} class="config-input" />
          </div>
          <div>
            <label for="space-cfg-ordinal" class="config-label">{$_("admin_space.config.fields.ordinal")}</label>
            <input id="space-cfg-ordinal" type="number" bind:value={spaceConfig.ordinal} class="config-input" />
          </div>
          <div class="sm:col-span-2">
            <label for="space-cfg-icon" class="config-label">{$_("admin_space.config.fields.icon")}</label>
            <input id="space-cfg-icon" type="text" bind:value={spaceConfig.icon} class="config-input" />
          </div>
          <div class="sm:col-span-2">
            <label for="space-cfg-signature" class="config-label">{$_("admin_space.config.fields.root_registration_signature")}</label>
            <input id="space-cfg-signature" type="text" bind:value={spaceConfig.root_registration_signature} class="config-input" />
          </div>
          <div class="sm:col-span-2">
            <label for="space-cfg-website" class="config-label">{$_("admin_space.config.fields.primary_website")}</label>
            <input id="space-cfg-website" type="url" placeholder="https://…" bind:value={spaceConfig.primary_website} class="config-input" />
          </div>
        </div>

        <div class="space-y-3">
          {#each listFields as field (field.key)}
            <div>
              <label for="space-cfg-{field.key}" class="config-label">
                {field.label()}
                <span class="font-normal text-text-faint">({field.hint ? field.hint() : $_("admin_space.config.comma_separated")})</span>
              </label>
              <input id="space-cfg-{field.key}" type="text" bind:value={spaceConfig[field.key]} class="config-input" />
            </div>
          {/each}
        </div>
      </form>
    {/if}

    {#snippet footer()}
      <button type="button" class="app-btn app-btn-secondary" onclick={closeSpaceConfigModal} disabled={isSavingSpaceConfig}>
        {$_("common.cancel")}
      </button>
      <button type="submit" form="space-config-form" class="app-btn app-btn-primary" disabled={isSavingSpaceConfig || isLoadingSpaceConfig} aria-busy={isSavingSpaceConfig}>
        {#if isSavingSpaceConfig}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
          {$_("common.saving")}
        {:else}
          {$_("common.save_changes")}
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}

<ConfirmDialog
  bind:open={showDeleteDialog}
  title={$_("delete_confirmation.title", { values: { type: itemToDelete?.resource_type ?? $_("delete_confirmation.item_label") } })}
  body={itemToDelete ? `${getDisplayName(itemToDelete)}\n${$_("delete_confirmation.warning")}` : ""}
  variant="danger"
  action={performDelete}
  onConfirm={afterDelete}
  onCancel={() => (itemToDelete = null)}
>
  <label class="flex items-start gap-2 text-sm text-text cursor-pointer">
    <input type="checkbox" class="mt-0.5 accent-primary" bind:checked={forceDelete} />
    <span>
      {$_("force_delete")}
      <span class="block text-xs text-text-muted">{$_("force_delete_help")}</span>
    </span>
  </label>
</ConfirmDialog>

<style>
  .config-label {
    display: block;
    margin-bottom: 0.25rem;
    font-size: 0.75rem;
    font-weight: 500;
    color: var(--color-text-muted);
  }

  .config-input {
    width: 100%;
    padding: 0.5rem 0.75rem;
    font-size: 0.875rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface-2);
    color: var(--color-text);
  }

  .config-input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
  }
</style>
