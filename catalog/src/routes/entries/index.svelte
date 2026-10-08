<script lang="ts">
  import { onMount } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { getMyEntities } from "@/lib/dmart_services";
  import { formatNumberInText } from "@/lib/helpers";
  import { toasts } from "@/lib/toast";
  import { log } from "@/lib/logger";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import { localized } from "@/lib/catalogItems";
  import { setTitle } from "@/lib/title";
  import { encodeSubpath, withBase } from "@/lib/paths";
  import { EditOutline, EyeOutline, PlusOutline, FileLinesOutline, UploadOutline, DownloadOutline } from "flowbite-svelte-icons";
  import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
  import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";
  import DataTable from "@/components/DataTable.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import CatalogToolbar, { type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import Badge from "@/components/ui/Badge.svelte";
  import IconButton from "@/components/ui/IconButton.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  interface EntryRow {
    resource_type: string;
    shortname: string;
    displayname: unknown;
    tags: string[];
    state: string | null;
    is_active: boolean;
    created_at: string;
    updated_at: string;
    space_name: string;
    subpath: string;
    owner_shortname: string;
  }

  type SortKey = "updated_at" | "created_at" | "title";

  let entities = $state<EntryRow[]>([]);
  let isLoading = $state(true);
  let loadError = $state<unknown>(null);
  let searchTerm = $state("");
  let resourceTypeFilter = $state("all");
  let spaceFilter = $state("all");
  let sortBy = $state<SortKey>("updated_at");
  let sortOrder = $state<SortOrder>("desc");
  let isCSVUploadModalOpen = $state(false);
  let isCSVDownloadModalOpen = $state(false);

  $effect(() => setTitle($_("my_entries.title")));

  const sortOptions = $derived([
    { value: "updated_at", label: $_("my_entries.sort.updated") },
    { value: "created_at", label: $_("my_entries.sort.created") },
    { value: "title", label: $_("my_entries.sort.title") },
  ]);

  const typeOptions = $derived([
    { value: "all", label: $_("my_entries.filters.all_types") },
    { value: "content", label: $_("admin_dashboard.filters.content") },
    { value: "media", label: $_("admin_dashboard.filters.media") },
    { value: "folder", label: $_("admin_dashboard.filters.folder") },
  ]);

  const indexAttributes = $derived([
    { key: "title", name: $_("my_entries.columns.entry") },
    { key: "resource_type", name: $_("my_entries.columns.type") },
    { key: "space_name", name: $_("my_entries.columns.space") },
    { key: "status", name: $_("my_entries.columns.status") },
    { key: "updated_at", name: $_("my_entries.columns.updated") },
  ]);

  function titleOf(entity: EntryRow): string {
    return localized(entity.displayname as never, $locale) || entity.shortname || $_("my_entries.untitled");
  }

  onMount(fetchEntities);

  async function fetchEntities() {
    isLoading = true;
    loadError = null;
    try {
      const records = await getMyEntities();
      entities = records
        .filter((entity) => entity?.resource_type !== "poll")
        .map((entity): EntryRow => {
          const attrs = (entity.attributes ?? {}) as Record<string, unknown>;
          return {
            resource_type: entity.resource_type || "",
            shortname: entity.shortname,
            displayname: attrs.displayname,
            tags: Array.isArray(attrs.tags) ? (attrs.tags as string[]) : [],
            state: typeof attrs.state === "string" ? attrs.state : null,
            is_active: attrs.is_active !== false,
            created_at: typeof attrs.created_at === "string" ? attrs.created_at : "",
            updated_at: typeof attrs.updated_at === "string" ? attrs.updated_at : "",
            space_name: typeof attrs.space_name === "string" ? attrs.space_name : "",
            subpath: entity.subpath || "",
            owner_shortname: typeof attrs.owner_shortname === "string" ? attrs.owner_shortname : "",
          };
        });
    } catch (error) {
      log.error("Error fetching entities:", error);
      loadError = error;
      toasts.error($_("my_entries.error.fetch_failed"));
      entities = [];
    } finally {
      isLoading = false;
    }
  }

  const availableSpaces = $derived.by(() => {
    const counts = new Map<string, number>();
    for (const e of entities) if (e.space_name) counts.set(e.space_name, (counts.get(e.space_name) ?? 0) + 1);
    return [...counts.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([shortname, entryCount]) => ({ shortname, entryCount }));
  });

  const filteredEntities = $derived.by(() => {
    const q = searchTerm.trim().toLowerCase();
    let filtered = entities;
    if (q) {
      filtered = filtered.filter(
        (e) =>
          titleOf(e).toLowerCase().includes(q) ||
          e.tags.some((tag) => tag.toLowerCase().includes(q)) ||
          e.resource_type.toLowerCase().includes(q) ||
          e.space_name.toLowerCase().includes(q),
      );
    }
    if (resourceTypeFilter !== "all") filtered = filtered.filter((e) => e.resource_type === resourceTypeFilter);
    if (spaceFilter !== "all") filtered = filtered.filter((e) => e.space_name === spaceFilter);

    const dir = sortOrder === "asc" ? 1 : -1;
    return [...filtered].sort((a, b) => {
      let av: string | number;
      let bv: string | number;
      switch (sortBy) {
        case "title":
          av = titleOf(a).toLowerCase();
          bv = titleOf(b).toLowerCase();
          break;
        case "created_at":
          av = new Date(a.created_at).getTime() || 0;
          bv = new Date(b.created_at).getTime() || 0;
          break;
        default:
          av = new Date(a.updated_at).getTime() || 0;
          bv = new Date(b.updated_at).getTime() || 0;
      }
      return av < bv ? -dir : av > bv ? dir : 0;
    });
  });

  const filtersActive = $derived(searchTerm.trim() !== "" || resourceTypeFilter !== "all" || spaceFilter !== "all");

  function clearAllFilters() {
    searchTerm = "";
    spaceFilter = "all";
    resourceTypeFilter = "all";
  }

  function viewHref(entity: EntryRow): string {
    return withBase(
      `/entries/${encodeURIComponent(entity.space_name)}/${encodeSubpath(entity.subpath)}/${encodeURIComponent(entity.shortname)}/${encodeURIComponent(entity.resource_type)}`,
    );
  }

  function viewEntity(entity: EntryRow, event?: MouseEvent | KeyboardEvent) {
    event?.preventDefault?.();
    goto("/entries/[space_name]/[subpath]/[shortname]/[resource_type]", {
      shortname: entity.shortname,
      space_name: entity.space_name,
      subpath: encodeSubpath(entity.subpath),
      resource_type: entity.resource_type,
    });
  }

  function editEntity(entity: EntryRow) {
    goto("/entries/[space_name]/[subpath]/[shortname]/[resource_type]/edit", {
      shortname: entity.shortname,
      space_name: entity.space_name,
      subpath: encodeSubpath(entity.subpath),
      resource_type: entity.resource_type,
    });
  }

  function isPublished(entity: EntryRow): boolean {
    return entity.is_active && entity.state !== "pending" && entity.state !== "rejected";
  }

  const csvSpace = $derived(spaceFilter !== "all" ? spaceFilter : availableSpaces[0]?.shortname || "catalog");
  const csvSpaces = $derived(availableSpaces.map((s) => ({ shortname: s.shortname, displayname: s.shortname })));
</script>

<div class="mx-auto max-w-7xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={$_("my_entries.title")} description={$_("my_entries.subtitle")} icon={FileLinesOutline}>
    {#snippet actions()}
      <IconButton label={$_("users_page.upload_csv")} variant="outline" onclick={() => (isCSVUploadModalOpen = true)}>
        <UploadOutline size="sm" />
      </IconButton>
      <IconButton label={$_("users_page.download_csv")} variant="outline" onclick={() => (isCSVDownloadModalOpen = true)}>
        <DownloadOutline size="sm" />
      </IconButton>
      <button type="button" class="app-btn app-btn-primary" onclick={() => goto("/entries/create")}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("my_entries.create_new")}
      </button>
    {/snippet}
  </PageHeader>

  {#if isLoading}
    <LoadingState />
  {:else if loadError}
    <ErrorState title={$_("my_entries.error.fetch_failed")} error={loadError} onRetry={fetchEntities} />
  {:else if entities.length === 0}
    <EmptyState icon={FileLinesOutline} title={$_("my_entries.empty.title")} hint={$_("my_entries.empty.hint")}>
      <button type="button" class="app-btn app-btn-primary app-btn-sm" onclick={() => goto("/entries/create")}>
        <PlusOutline size="sm" aria-hidden="true" />
        {$_("my_entries.empty.action")}
      </button>
    </EmptyState>
  {:else}
    <div class="flex flex-wrap items-center gap-2 mb-4" role="group" aria-label={$_("statistics")}>
      <Badge>{$_("my_entries.stats.total", { values: { count: formatNumberInText(entities.length, $locale ?? "") } })}</Badge>
      <Badge variant="success">
        {$_("my_entries.stats.spaces", { values: { count: formatNumberInText(availableSpaces.length, $locale ?? "") } })}
      </Badge>
    </div>

    <CatalogToolbar
      class="mb-4"
      bind:search={searchTerm}
      placeholder={$_("route_labels.placeholder_search_entries")}
      onSearch={(q) => (searchTerm = q)}
      bind:sort={sortBy}
      {sortOptions}
      bind:order={sortOrder}
    >
      {#snippet filters()}
        <label for="entry-type-filter" class="sr-only">{$_("my_entries.columns.type")}</label>
        <select id="entry-type-filter" bind:value={resourceTypeFilter} class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary">
          {#each typeOptions as option (option.value)}
            <option value={option.value}>{option.label}</option>
          {/each}
        </select>
        <label for="entry-space-filter" class="sr-only">{$_("my_entries.columns.space")}</label>
        <select id="entry-space-filter" bind:value={spaceFilter} class="h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary">
          <option value="all">{$_("my_entries.filters.all_spaces")}</option>
          {#each availableSpaces as space (space.shortname)}
            <option value={space.shortname}>{space.shortname} ({space.entryCount})</option>
          {/each}
        </select>
        {#if filtersActive}
          <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={clearAllFilters}>
            {$_("search_filters.clear_filters")}
          </button>
        {/if}
      {/snippet}
    </CatalogToolbar>

    <DataTable
      items={filteredEntities}
      {indexAttributes}
      rowHref={viewHref}
      rowLabel={titleOf}
      rowKey={(e: EntryRow) => `${e.space_name}/${e.subpath}/${e.resource_type}/${e.shortname}`}
      onRowClick={viewEntity}
      totalItems={filteredEntities.length}
      itemsPerPage={filteredEntities.length || 1}
      emptyMessage={$_("my_entries.no_match")}
    >
      {#snippet cell({ item, attr })}
        {#if attr.key === "title"}
          <div class="min-w-0 max-w-xs">
            <div class="font-medium text-text truncate">{titleOf(item)}</div>
            {#if item.tags.length > 0}
              <div class="text-xs text-text-faint truncate">{item.tags.map((t: string) => `#${t}`).join(" ")}</div>
            {/if}
          </div>
        {:else if attr.key === "resource_type"}
          <Badge size="sm" variant={item.resource_type === "content" ? "info" : item.resource_type === "media" ? "primary" : "neutral"}>
            {item.resource_type || $_("common.unknown")}
          </Badge>
        {:else if attr.key === "space_name"}
          <Badge size="sm">{item.space_name}</Badge>
        {:else if attr.key === "status"}
          <Badge size="sm" variant={isPublished(item) ? "success" : "warning"}>
            {isPublished(item) ? $_("my_entries.status.active") : $_("my_entries.status.draft")}
          </Badge>
        {:else if attr.key === "updated_at"}
          <span class="text-text-muted whitespace-nowrap">{formatDate(item.updated_at, "datetime", $locale)}</span>
        {/if}
      {/snippet}

      {#snippet actions({ item })}
        <IconButton label="{$_('actions.view')} {titleOf(item)}" size="sm" href={viewHref(item)} onclick={(e: MouseEvent) => viewEntity(item, e)}>
          <EyeOutline size="sm" />
        </IconButton>
        <IconButton label="{$_('common.edit')} {titleOf(item)}" size="sm" onclick={() => editEntity(item)}>
          <EditOutline size="sm" />
        </IconButton>
      {/snippet}
    </DataTable>

    <p class="mt-3 text-xs text-text-muted tabular-nums">
      {$_("my_entries.showing", { values: { shown: filteredEntities.length, total: entities.length } })}
    </p>
  {/if}
</div>

<ModalCSVUpload space_name={csvSpace} subpath="/" bind:isOpen={isCSVUploadModalOpen} onUploadSuccess={fetchEntities} availableSpaces={csvSpaces} />
<ModalCSVDownload space_name={csvSpace} subpath="/" bind:isOpen={isCSVDownloadModalOpen} availableSpaces={csvSpaces} />
