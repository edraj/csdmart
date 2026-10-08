<script lang="ts">
  import { isTotalUnknown, resolveTotal } from "@shared/query-total";
  import { onDestroy } from "svelte";
  import { get } from "svelte/store";
  import { params } from "@roxi/routify";
  import { ResourceType, SortType, type DmartScope } from "@edraj/tsdmart";
  import {
    ChevronRightOutline,
    DownloadOutline,
    FlagOutline,
    FolderOpenOutline,
    ShareNodesOutline,
    UploadOutline,
  } from "flowbite-svelte-icons";
  import {
    buildHideFoldersSearch,
    getEntity,
    getSpaceContents,
    getSpaces,
    mergeSearch,
  } from "@/lib/dmart_services";
  import { getAvatarsCached } from "@/lib/dmart_services/avatars";
  import { _, locale } from "@/i18n";
  import { formatNumberInText } from "@/lib/helpers";
  import { absoluteUrl, catalogBreadcrumbs, catalogPath, decodeSubpath, withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";
  import { copyToClipboard } from "@/lib/toast";
  import {
    attachmentCounts,
    itemDescription,
    itemTitle,
    localized,
    previewText,
    tagsOf,
    type AttachmentCounts,
    type CatalogRecord,
    type Localized,
  } from "@/lib/catalogItems";
  import { getCurrentScope, user } from "@/stores/user";
  import { getWebSocketService } from "@/lib/services/websocket";
  import { isJsonObject } from "@/lib/types";
  import CatalogToolbar, { type SortOption, type SortOrder } from "@/components/ui/CatalogToolbar.svelte";
  import type { MenuItem } from "@/components/ui/DropdownMenu.svelte";
  import EmptyState from "@/components/ui/EmptyState.svelte";
  import ErrorState from "@/components/ui/ErrorState.svelte";
  import LoadingState from "@/components/ui/LoadingState.svelte";
  import PageHeader from "@/components/ui/PageHeader.svelte";
  import PostCard from "@/components/post/PostCard.svelte";
  import PostCardSkeleton from "@/components/post/PostCardSkeleton.svelte";
  import ReportModal from "@/components/ReportModal.svelte";
  import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
  import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";

  // A list row: the record plus what the card needs, computed once when the
  // page arrives (counts from the attachments already in the response, the
  // 150-character preview, clean tags); the avatar is filled in by one later
  // state update per page.
  interface FolderItem {
    record: CatalogRecord;
    key: string;
    owner: string;
    isFolder: boolean;
    counts: AttachmentCounts;
    tags: string[];
    preview: string;
    avatarUrl: string | null;
  }

  // The retrieve-entry shape: attributes flattened onto the record.
  interface FolderEntry {
    displayname?: Localized;
    description?: Localized;
    payload?: { body?: { allow_upload_csv?: boolean; allow_csv?: boolean; stream?: boolean } };
  }

  interface SpaceRecord extends CatalogRecord {
    attributes?: CatalogRecord["attributes"] & { hide_folders?: unknown };
  }

  let isLoading = $state(false);
  let isLoadingMore = $state(false);
  let allContents = $state<FolderItem[]>([]);
  let error = $state<unknown>(null);
  let spaceName = $state("");
  // API-style subpath with a leading slash ("/" for the root), decoded from
  // the dash-encoded route segment.
  let actualSubpath = $state("/");

  let itemsPerLoad = $state(10);
  let currentOffset = $state(0);
  let totalItemsCount = $state(0);
  // False when the server skipped counting (total -1, e.g.
  // RETRIEVE_TOTAL_DEFAULT=false). The count is then hidden rather than shown
  // as 0, and emptiness is judged by the rows actually received.
  let totalKnown = $state(true);
  let hasMoreServerItems = $state(true);

  // Drops a response that arrives after a newer request was issued.
  let loadSeq = 0;

  let showReportModal = $state(false);
  let reportItem = $state<FolderItem | null>(null);
  let reportSubpath = $state("/");

  // The toolbar binds `searchQuery` as the user types; `activeSearch` is what
  // was submitted and sent to the server.
  let searchQuery = $state("");
  let activeSearch = $state("");
  let sortBy = $state("name");
  let sortOrder = $state<SortOrder>("asc");
  let selectedTags = $state<string[]>([]);
  let showAllTags = $state(false);
  let filterType = $state("all");
  let filterStatus = $state("all");

  // Folder metadata for CSV permissions; the space record for hide_folders
  // and the space's display name.
  let folderMetadata = $state<FolderEntry | null>(null);
  let spaceRecord = $state<SpaceRecord | null>(null);
  let spaceHideFolders = $state<string[]>([]);
  let isCSVUploadModalOpen = $state(false);
  let isCSVDownloadModalOpen = $state(false);

  const canUploadCSV = $derived(folderMetadata?.payload?.body?.allow_upload_csv === true);
  const canDownloadCSV = $derived(folderMetadata?.payload?.body?.allow_csv === true);
  const streamEnabled = $derived(folderMetadata?.payload?.body?.stream === true);

  const itemsPerLoadOptions = [10, 25, 50];

  const sortOptions = $derived<SortOption[]>([
    { value: "name", label: $_("admin_dashboard.sort.name") },
    { value: "created", label: $_("admin_dashboard.sort.created") },
    { value: "updated", label: $_("admin_dashboard.sort.updated") },
    { value: "owner", label: $_("admin_dashboard.sort.owner") },
  ]);

  // The server sorts the whole folder; the client never re-sorts a page.
  const SERVER_SORT_FIELD: Record<string, string> = {
    name: "shortname",
    created: "created_at",
    updated: "updated_at",
    owner: "owner_shortname",
  };

  const selectClass =
    "h-9 ps-3 pe-8 text-sm rounded-control border border-border bg-surface-2 text-text focus:border-primary focus:ring-1 focus:ring-primary";

  const isRoot = $derived(actualSubpath === "/");
  const spaceTitle = $derived(localized(spaceRecord?.attributes?.displayname, $locale) || spaceName);
  const folderShortname = $derived(actualSubpath.split("/").filter(Boolean).pop() ?? "");
  const folderTitle = $derived(isRoot ? spaceTitle : localized(folderMetadata?.displayname, $locale) || folderShortname);
  const folderDescription = $derived(
    localized(folderMetadata?.description, $locale) ||
      `${$_("catalog_contents.browse_contents")} ${spaceTitle}${isRoot ? "" : ` · ${actualSubpath}`}`,
  );

  // Rebuilt from the route and the locale, so the "Catalogs" root crumb and
  // the space's display name follow a language switch.
  const breadcrumbs = $derived.by(() => {
    if (!spaceName) return [];
    const crumbs = catalogBreadcrumbs({
      space: spaceName,
      subpath: actualSubpath,
      catalogsLabel: $_("post_detail.breadcrumb.catalogs"),
    });
    return crumbs.map((crumb, i) => (i === 1 ? { ...crumb, name: spaceTitle } : crumb));
  });

  $effect(() => {
    if (isRoot) setTitle(spaceTitle);
    else setTitle(folderTitle, spaceTitle);
  });

  const menuItems = $derived<MenuItem[]>([
    { id: "share", label: $_("catalog_contents.card.share"), icon: ShareNodesOutline },
    { id: "report", label: $_("catalog_contents.card.report"), icon: FlagOutline },
  ]);
  const folderMenuItems = $derived<MenuItem[]>([{ id: "share", label: $_("catalog_contents.card.share"), icon: ShareNodesOutline }]);

  // Client-side refinements of the loaded page (the server did search + sort).
  const availableTags = $derived([...new Set(allContents.flatMap((item) => item.tags))].sort());
  const displayedTags = $derived(showAllTags ? availableTags : availableTags.slice(0, 10));
  const resourceTypes = $derived([...new Set(allContents.map((item) => item.record.resource_type ?? ""))].filter(Boolean));

  const filteredContents = $derived.by(() => {
    let filtered = allContents;
    // `hide_folders` is applied server-side via `-@shortname:...` in loadContents.
    if (filterType !== "all") filtered = filtered.filter((item) => item.record.resource_type === filterType);
    if (filterStatus !== "all") {
      filtered = filtered.filter((item) =>
        filterStatus === "active" ? item.record.attributes?.is_active !== false : item.record.attributes?.is_active === false,
      );
    }
    if (selectedTags.length > 0) {
      const wanted = selectedTags.map((t) => t.toLowerCase());
      filtered = filtered.filter((item) => {
        const have = item.tags.map((t) => t.toLowerCase());
        return wanted.every((tag) => have.includes(tag));
      });
    }
    return filtered;
  });

  const hasActiveFilters = $derived(
    !!activeSearch || selectedTags.length > 0 || filterType !== "all" || filterStatus !== "all",
  );

  // ---- WebSocket stream subscription (gated by the folder's `stream` flag) ----
  let removeStreamListener: (() => void) | null = null;
  let streamSubscribedKey: string | null = null;

  function buildStreamKey(space: string, path: string) {
    return `${space}::${path}`;
  }

  async function teardownStream() {
    const ws = getWebSocketService();
    if (!ws) {
      removeStreamListener = null;
      streamSubscribedKey = null;
      return;
    }
    if (removeStreamListener) {
      try {
        removeStreamListener();
      } catch {
        /* ignore */
      }
      removeStreamListener = null;
    }
    if (streamSubscribedKey) {
      const [space, path] = streamSubscribedKey.split("::");
      ws.unsubscribe(space, path);
      // Restore the user's personal subscription so global notifications keep working.
      const shortname = get(user)?.shortname;
      if (shortname) {
        await ws.subscribe("personal", `/people/${shortname}`);
      }
      streamSubscribedKey = null;
    }
  }

  async function setupStream(space: string, path: string) {
    const ws = getWebSocketService();
    if (!ws) return;
    const key = buildStreamKey(space, path);
    if (streamSubscribedKey === key) return;
    await teardownStream();
    const subscribed = await ws.subscribe(space, path);
    if (!subscribed) return;
    streamSubscribedKey = key;
    removeStreamListener = ws.addMessageListener((data) => {
      const action = isJsonObject(data.message) ? data.message.action_type : undefined;
      if (
        data.type === "notification_subscription" &&
        typeof action === "string" &&
        ["create", "update", "delete"].includes(action)
      ) {
        void loadContents(true);
      }
    });
  }

  onDestroy(() => {
    void teardownStream();
  });

  $effect(() => {
    const path = actualSubpath;
    if (streamEnabled && spaceName && actualSubpath) {
      void setupStream(spaceName, path);
    } else if (streamSubscribedKey) {
      void teardownStream();
    }
  });

  // ---- Loading ----
  let prevParamsKey = "";

  $effect(() => {
    const space = $params.space_name;
    const segment = $params.subpath;
    if (!space || !segment) return;
    const key = `${space}|${segment}`;
    if (key === prevParamsKey) return;
    prevParamsKey = key;
    void initializeContent(space, segment);
  });

  async function initializeContent(space: string, segment: string) {
    spaceName = space;
    actualSubpath = decodeSubpath(segment);
    const scope = getCurrentScope();
    // Resolve the space-level hide list BEFORE fetching contents so the
    // server-side `-@shortname:...` filter lands on the first query.
    await Promise.all([loadFolderMetadata(scope), loadSpaceRecord(scope)]);
    await loadContents(true);
  }

  // One spaces query gives both the `hide_folders` list and the display name.
  async function loadSpaceRecord(scope: DmartScope) {
    try {
      const response = await getSpaces(false, scope);
      const match = (response.records as SpaceRecord[]).find((record) => record.shortname === spaceName) ?? null;
      spaceRecord = match;
      const hide = match?.attributes?.hide_folders;
      spaceHideFolders = Array.isArray(hide) ? hide.filter((h): h is string => typeof h === "string") : [];
    } catch (err) {
      console.warn("Could not resolve the space record:", err);
      spaceRecord = null;
      spaceHideFolders = [];
    }
  }

  async function loadFolderMetadata(scope: DmartScope) {
    // Only a sub-folder has an entry of its own; the root is the space.
    const pathParts = actualSubpath.split("/").filter((p) => p.length > 0);
    if (pathParts.length === 0) {
      folderMetadata = null;
      return;
    }
    try {
      const shortname = pathParts[pathParts.length - 1];
      const parentSubpath = pathParts.length > 1 ? "/" + pathParts.slice(0, -1).join("/") : "/";
      folderMetadata = (await getEntity(shortname, spaceName, parentSubpath, ResourceType.folder, scope, true, false)) as
        | FolderEntry
        | null;
    } catch (err) {
      console.error("Error fetching folder metadata:", err);
      folderMetadata = null;
    }
  }

  function toItem(record: CatalogRecord): FolderItem {
    const isFolder = record.resource_type === "folder";
    return {
      record,
      key: `${record.subpath ?? actualSubpath}/${record.shortname}`,
      owner: record.attributes?.owner_shortname ?? "",
      isFolder,
      counts: attachmentCounts(record.attachments),
      tags: tagsOf(record),
      // A folder's payload is its configuration; its description is the preview.
      preview: isFolder ? itemDescription(record, $locale) : previewText(record.attributes?.payload),
      avatarUrl: null,
    };
  }

  // Avatars for every owner on the page: one cached lookup per owner, then ONE
  // assignment — the rows are already on screen by the time this runs.
  async function attachAvatars(items: FolderItem[], seq: number) {
    const urls = await getAvatarsCached(items.map((item) => item.owner));
    if (seq !== loadSeq) return;
    allContents = allContents.map((item) =>
      item.avatarUrl === null && urls.has(item.owner) ? { ...item, avatarUrl: urls.get(item.owner) ?? null } : item,
    );
  }

  async function loadContents(reset = false) {
    const seq = ++loadSeq;
    // A reset keeps the current rows under an overlay until the new page
    // arrives; it never blanks the list to a spinner.
    if (reset) {
      isLoading = true;
      currentOffset = 0;
    } else {
      isLoadingMore = true;
    }
    error = null;

    try {
      const response = await getSpaceContents(
        spaceName,
        actualSubpath,
        getCurrentScope(),
        itemsPerLoad,
        reset ? 0 : currentOffset,
        false,
        undefined,
        mergeSearch(activeSearch, buildHideFoldersSearch(spaceHideFolders)),
        SERVER_SORT_FIELD[sortBy] ?? "shortname",
        sortOrder === "desc" ? SortType.descending : SortType.ascending,
      );
      if (seq !== loadSeq) return;

      totalKnown = !isTotalUnknown(response?.attributes?.total);
      totalItemsCount = resolveTotal(response?.attributes?.total);

      if (response && response.records) {
        const items = (response.records as CatalogRecord[]).map(toItem);
        allContents = reset ? items : [...allContents, ...items];
        hasMoreServerItems = items.length === itemsPerLoad;
        currentOffset = (reset ? 0 : currentOffset) + items.length;
        void attachAvatars(items, seq);
      } else {
        if (reset) allContents = [];
        hasMoreServerItems = false;
      }
    } catch (err) {
      if (seq !== loadSeq) return;
      console.error("Error fetching space contents:", err);
      error = err;
      if (reset) allContents = [];
      hasMoreServerItems = false;
    } finally {
      if (seq === loadSeq) {
        isLoading = false;
        isLoadingMore = false;
      }
    }
  }

  function loadMoreItems() {
    if (isLoadingMore || isLoading || !hasMoreServerItems) return;
    void loadContents(false);
  }

  function handleItemsPerLoadChange(event: Event) {
    itemsPerLoad = parseInt((event.currentTarget as HTMLSelectElement).value, 10) || 10;
    void loadContents(true);
  }

  function handleSearch(query: string) {
    activeSearch = query.trim();
    void loadContents(true);
  }

  function clearSearch() {
    searchQuery = "";
    if (!activeSearch) return;
    activeSearch = "";
    void loadContents(true);
  }

  function itemPath(item: FolderItem): string {
    if (item.isFolder) {
      return catalogPath({
        space: spaceName,
        subpath: `${actualSubpath}/${item.record.shortname}`,
      });
    }
    return catalogPath({
      space: spaceName,
      subpath: item.record.subpath || actualSubpath,
      shortname: item.record.shortname,
      resourceType: item.record.resource_type ?? "content",
    });
  }

  function clearFilters() {
    searchQuery = "";
    activeSearch = "";
    selectedTags = [];
    sortBy = "name";
    sortOrder = "asc";
    filterType = "all";
    filterStatus = "all";
    // Search and sort are applied by the server, so clearing them re-queries.
    void loadContents(true);
  }

  function toggleTag(tag: string) {
    selectedTags = selectedTags.includes(tag) ? selectedTags.filter((t) => t !== tag) : [...selectedTags, tag];
  }

  async function shareItem(item: FolderItem) {
    const url = absoluteUrl(itemPath(item));
    const title = itemTitle(item.record, $locale);
    if (typeof navigator !== "undefined" && navigator.share) {
      try {
        await navigator.share({
          title,
          text: $_("catalog_contents.card.share_text", { values: { title } }),
          url,
        });
        return;
      } catch (err) {
        // The user dismissed the share sheet; nothing to report.
        if ((err as { name?: string })?.name === "AbortError") return;
      }
    }
    await copyToClipboard(url, { copied: $_("ui.link_copied"), failed: $_("ui.copy_failed") });
  }

  function openReportModal(item: FolderItem) {
    reportItem = item;
    reportSubpath = item.record.subpath || actualSubpath;
    showReportModal = true;
  }

  function onCardMenu(item: FolderItem, action: string) {
    if (action === "share") void shareItem(item);
    else if (action === "report") openReportModal(item);
  }

  const number = (n: number) => formatNumberInText(n, $locale ?? "");
</script>

<div class="mx-auto w-full max-w-5xl px-4 sm:px-6 py-6 sm:py-8">
  <PageHeader title={folderTitle} description={folderDescription} icon={FolderOpenOutline}>
    {#snippet breadcrumb()}
      <nav aria-label={$_("ui.breadcrumb")}>
        <ol class="flex flex-wrap items-center gap-x-1 gap-y-0.5 text-sm">
          {#each breadcrumbs as crumb, i (`${i}:${crumb.path ?? crumb.name}`)}
            <li class="inline-flex items-center gap-1 min-w-0">
              {#if i > 0}
                <ChevronRightOutline size="xs" class="rtl:rotate-180 text-text-faint shrink-0" aria-hidden="true" />
              {/if}
              {#if crumb.path}
                <a href={withBase(crumb.path)} class="text-text-muted hover:text-primary hover:underline rounded-control">
                  {crumb.name}
                </a>
              {:else}
                <span aria-current="page" class="font-medium text-text truncate max-w-[16rem]">{crumb.name}</span>
              {/if}
            </li>
          {/each}
        </ol>
      </nav>
    {/snippet}
    {#snippet actions()}
      {#if canUploadCSV}
        <button type="button" class="app-btn app-btn-secondary" onclick={() => (isCSVUploadModalOpen = true)}>
          <UploadOutline size="sm" aria-hidden="true" />
          {$_("catalog_contents.csv.import")}
        </button>
      {/if}
      {#if canDownloadCSV}
        <button type="button" class="app-btn app-btn-secondary" onclick={() => (isCSVDownloadModalOpen = true)}>
          <DownloadOutline size="sm" aria-hidden="true" />
          {$_("catalog_contents.csv.export")}
        </button>
      {/if}
    {/snippet}
  </PageHeader>

  {#if availableTags.length > 0}
    <section class="mb-5" aria-label={$_("catalog_contents.tags.available_tags")}>
      <div class="flex flex-wrap items-center justify-between gap-2 mb-2">
        <p class="text-xs font-medium text-text-muted uppercase tracking-wide">{$_("catalog_contents.tags.available_tags")}</p>
        {#if selectedTags.length > 0}
          <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={() => (selectedTags = [])}>
            {$_("catalog_contents.tags.clear_all")}
          </button>
        {/if}
      </div>
      <div class="flex flex-wrap gap-2">
        {#each displayedTags as tag (tag)}
          {@const selected = selectedTags.includes(tag)}
          <button
            type="button"
            class="tag-pill"
            class:selected
            aria-pressed={selected}
            aria-label={$_("catalog_contents.tags.filter_by_tag_aria", { values: { tag } })}
            onclick={() => toggleTag(tag)}
          >
            #{tag}
          </button>
        {/each}
        {#if availableTags.length > 10}
          <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={() => (showAllTags = !showAllTags)}>
            {showAllTags ? $_("catalog_contents.tags.show_less") : $_("catalog_contents.tags.show_all")}
            ({number(availableTags.length)})
          </button>
        {/if}
      </div>
    </section>
  {/if}

  <!-- The toolbar is always available: an empty folder (or an unknown total)
       must not take the search box away. -->
  <CatalogToolbar
    bind:search={searchQuery}
    placeholder={$_("catalog_contents.search.placeholder")}
    onSearch={handleSearch}
    onClear={clearSearch}
    bind:sort={sortBy}
    {sortOptions}
    onSortChange={() => void loadContents(true)}
    bind:order={sortOrder}
    onOrderChange={() => void loadContents(true)}
    class="mb-4"
  >
    {#snippet filters()}
      <label for="type-filter-select" class="sr-only">{$_("catalog_contents.filters.type")}</label>
      <select id="type-filter-select" bind:value={filterType} class={selectClass} title={$_("catalog_contents.filters.type")}>
        <option value="all">{$_("catalog_contents.filters.all_types")}</option>
        {#each resourceTypes as type (type)}
          <option value={type}>{type}</option>
        {/each}
      </select>
      <label for="status-filter-select" class="sr-only">{$_("catalog_contents.filters.status")}</label>
      <select id="status-filter-select" bind:value={filterStatus} class={selectClass} title={$_("catalog_contents.filters.status")}>
        <option value="all">{$_("catalog_contents.filters.all_statuses")}</option>
        <option value="active">{$_("catalog_contents.filters.active")}</option>
        <option value="inactive">{$_("catalog_contents.filters.inactive")}</option>
      </select>
      <label for="items-per-load-select" class="sr-only">{$_("catalog_contents.infinite_scroll.items_per_load")}</label>
      <select
        id="items-per-load-select"
        value={itemsPerLoad}
        onchange={handleItemsPerLoadChange}
        class={selectClass}
        title={$_("catalog_contents.infinite_scroll.items_per_load")}
      >
        {#each itemsPerLoadOptions as option (option)}
          <option value={option}>{option}</option>
        {/each}
      </select>
    {/snippet}
    {#if hasActiveFilters}
      <button type="button" class="app-btn app-btn-ghost app-btn-sm" onclick={clearFilters}>
        {$_("catalog_contents.filters.clear_all")}
      </button>
    {/if}
  </CatalogToolbar>

  {#if isLoading && allContents.length === 0}
    <div class="card-list" role="status" aria-busy="true" aria-label={$_("catalog_contents.loading")}>
      {#each Array.from({ length: 4 }, (_, i) => i) as i (i)}
        <PostCardSkeleton />
      {/each}
    </div>
  {:else if error}
    <ErrorState title={$_("catalog_contents.error.title")} {error} onRetry={() => void loadContents(true)} />
  {:else}
    {#if allContents.length > 0}
      <p class="text-sm text-text-muted mb-4" aria-live="polite">
        {#if totalKnown}
          {$_("catalog_contents.infinite_scroll.showing_items", {
            values: { displayed: number(filteredContents.length), total: number(totalItemsCount) },
          })}
        {:else}
          {$_("catalog_contents.infinite_scroll.showing_count", {
            values: { displayed: number(filteredContents.length) },
          })}
        {/if}
        {#if activeSearch}
          {$_("catalog_contents.results.for_query", { values: { query: activeSearch } })}
        {/if}
        {#if selectedTags.length > 0}
          {$_("catalog_contents.results.with_tags", { values: { count: number(selectedTags.length) } })}
        {/if}
      </p>
    {/if}

    {#if allContents.length === 0}
      <EmptyState
        icon={FolderOpenOutline}
        title={$_("catalog_contents.empty.title")}
        hint={hasActiveFilters ? $_("catalog_contents.empty.no_matches") : $_("catalog_contents.empty.folder_empty")}
      >
        {#if hasActiveFilters}
          <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearFilters}>
            {$_("catalog_contents.filters.clear_all")}
          </button>
        {/if}
      </EmptyState>
    {:else if filteredContents.length === 0}
      <EmptyState title={$_("catalog_contents.empty.title")} hint={$_("catalog_contents.empty.no_matches")}>
        <button type="button" class="app-btn app-btn-secondary app-btn-sm" onclick={clearFilters}>
          {$_("catalog_contents.filters.clear_all")}
        </button>
      </EmptyState>
    {:else}
      <LoadingState variant="overlay" loading={isLoading || isLoadingMore} label={$_("catalog_contents.infinite_scroll.loading")}>
        <div class="card-list">
          {#each filteredContents as item (item.key)}
            <PostCard
              href={withBase(itemPath(item))}
              title={itemTitle(item.record, $locale)}
              author={item.owner || $_("common.unknown")}
              avatarUrl={item.avatarUrl}
              date={item.record.attributes?.created_at}
              isFolder={item.isFolder}
              preview={item.preview}
              tags={item.tags}
              {selectedTags}
              onTagClick={toggleTag}
              counts={item.isFolder ? undefined : item.counts}
              menuItems={item.isFolder ? folderMenuItems : menuItems}
              onMenuSelect={(action) => onCardMenu(item, action)}
            />
          {/each}
        </div>
      </LoadingState>

      {#if hasMoreServerItems}
        <div class="flex justify-center mt-6">
          <button type="button" class="app-btn app-btn-secondary" onclick={loadMoreItems} disabled={isLoadingMore || isLoading}>
            {$_("catalog_contents.infinite_scroll.load_more")}
          </button>
        </div>
      {:else}
        <p class="end-of-results">
          {$_("catalog_contents.infinite_scroll.end_of_results")}
          {#if totalKnown}
            · {$_("catalog_contents.infinite_scroll.total_items", { values: { count: number(totalItemsCount) } })}
          {/if}
        </p>
      {/if}
    {/if}
  {/if}
</div>

<!-- CSV Import/Export Modals -->
<ModalCSVUpload
  space_name={spaceName}
  subpath={actualSubpath}
  bind:isOpen={isCSVUploadModalOpen}
  onUploadSuccess={() => loadContents(true)}
/>

<ModalCSVDownload space_name={spaceName} subpath={actualSubpath} bind:isOpen={isCSVDownloadModalOpen} />

<ReportModal
  bind:isVisible={showReportModal}
  entryShortname={reportItem?.record.shortname || ""}
  entryTitle={reportItem ? itemTitle(reportItem.record, $locale) : ""}
  {spaceName}
  subpath={reportSubpath}
  onClose={() => {
    showReportModal = false;
    reportItem = null;
  }}
  onReportSubmitted={() => {
    showReportModal = false;
    reportItem = null;
  }}
/>

<style>
  .card-list {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }

  .tag-pill {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.25rem 0.75rem;
    border-radius: var(--radius-full);
    border: 1px solid var(--color-border);
    background: var(--color-surface-2);
    color: var(--color-text-muted);
    font-size: var(--font-size-sm);
    font-weight: var(--font-weight-medium);
    cursor: pointer;
    transition: background var(--duration-fast) var(--ease-out), color var(--duration-fast) var(--ease-out),
      border-color var(--duration-fast) var(--ease-out);
  }

  .tag-pill:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .tag-pill.selected {
    background: var(--color-primary-soft);
    border-color: var(--color-primary);
    color: var(--color-primary);
  }

  .end-of-results {
    margin: 2.5rem 0 1rem;
    text-align: center;
    font-size: var(--font-size-xs);
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--color-text-faint);
  }
</style>
