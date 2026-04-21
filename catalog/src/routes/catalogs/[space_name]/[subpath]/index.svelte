<script lang="ts">
    import {onDestroy} from "svelte";
    import {goto, params} from "@roxi/routify";
    import {getAvatar, getSpaceContents, getEntity, getSpaceHideFolders, buildHideFoldersSearch, mergeSearch} from "@/lib/dmart_services";
    import {_, locale} from "@/i18n";
    import Avatar from "@/components/Avatar.svelte";
    import {derived as derivedStore, get} from "svelte/store";
    import {ResourceType} from "@edraj/tsdmart";
    import {getCurrentScope, user} from "@/stores/user";
    import {UploadOutline, DownloadOutline} from "flowbite-svelte-icons";
    import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
    import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";
    import {withBasePrefix} from "@/lib/basePath";
    import {getWebSocketService} from "@/lib/services/websocket";

    $goto;

  let isLoading = $state(false);
  let isLoadingMore = $state(false);
  let allContents = $state<any[]>([]);
  let error: any = $state(null);
  let spaceName = $state("");
  let subpath = $state("");
  let actualSubpath = $state("");
  let breadcrumbs: any[] = $state([]);

  let itemsPerLoad = $state(10);
  let currentOffset = $state(0);
  let totalItemsCount = $state(0);
  let hasMoreServerItems = $state(true);

  let searchQuery = $state("");
  let sortBy = $state("name");
  let sortOrder = $state("asc");
  let selectedTags = $state<any[]>([]);
  let availableTags = $state<any[]>([]);
  let showAllTags = $state(false);
  let showFilters = $state(false);
  let filterType = $state("all");
  let filterStatus = $state("all");

  // Folder metadata for CSV permissions
  let folderMetadata: any = $state(null);
  let spaceHideFolders = $state<string[]>([]);
  let isCSVUploadModalOpen = $state(false);
  let isCSVDownloadModalOpen = $state(false);

  // Computed permissions
  let canUploadCSV = $derived(folderMetadata?.payload?.body?.allow_upload_csv === true);
  let canDownloadCSV = $derived(folderMetadata?.payload?.body?.allow_csv === true);
  let streamEnabled = $derived(folderMetadata?.payload?.body?.stream === true);

  // WebSocket stream subscription (gated by folder's `stream` flag)
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
      try { removeStreamListener(); } catch { /* ignore */ }
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
      if (
        data.type === "notification_subscription" &&
        data.message?.action_type &&
        ["create", "update", "delete"].includes(data.message.action_type)
      ) {
        loadContents(true);
      }
    });
  }

  const isRTL = derivedStore(
    locale,
    ($locale: any) => $locale === "ar" || $locale === "ku"
  );
  const itemsPerLoadOptions = [10, 25, 50];

  function getResourceTypes() {
    const types = [...new Set(allContents.map((item: any) => item.resource_type))];
    return types;
  }

  const sortOptions = [
    { value: "name", label: $_("admin_dashboard.sort.name") },
    { value: "created", label: $_("admin_dashboard.sort.created") },
    { value: "updated", label: $_("admin_dashboard.sort.updated") },
    { value: "owner", label: $_("admin_dashboard.sort.owner") },
  ];

  async function initializeContent() {
    spaceName = $params.space_name;
    subpath = $params.subpath;
    actualSubpath = subpath.replace(/-/g, "/");

    const pathParts = actualSubpath
      .split("/")
      .filter((part) => part.length > 0);
    breadcrumbs = [
      { name: spaceName, path: `${spaceName}` },
      { name: spaceName, path: `/${spaceName}/${subpath}` },
    ];

    let currentPath = "";
    let currentUrlPath = "";
    pathParts.forEach((part, index) => {
      currentPath += `/${part}`;
      currentUrlPath += (index === 0 ? "" : "-") + part;
      breadcrumbs.push({
        name: part,
        path:
          index === pathParts.length - 1
            ? null
            : `/${spaceName}/${subpath}/${currentUrlPath}`,
      });
    });

    // Resolve the space-level hide list BEFORE fetching contents so the
    // server-side `-@shortname:...` filter lands on the first query.
    await Promise.all([
      loadFolderMetadata(),
      loadSpaceHideFolders(),
    ]);
    await loadContents(true);
  }

  async function loadSpaceHideFolders() {
    spaceHideFolders = await getSpaceHideFolders(spaceName, getCurrentScope());
  }

  async function loadFolderMetadata() {
    // Only fetch folder metadata if we're in a subpath (not root)
    if (!actualSubpath || actualSubpath === "" || actualSubpath === "/") {
      folderMetadata = null;
      return;
    }

    try {
      // Get the parent path and folder shortname
      const pathParts = actualSubpath.split("/").filter(p => p.length > 0);
      if (pathParts.length === 0) {
        folderMetadata = null;
        return;
      }

      const folderShortname = pathParts[pathParts.length - 1];
      const parentSubpath = pathParts.length > 1 
        ? "/" + pathParts.slice(0, -1).join("/")
        : "/";

      const folder = await getEntity(
        folderShortname,
        spaceName,
        parentSubpath,
        ResourceType.folder,
        getCurrentScope(),
        true,
        false
      );

      folderMetadata = folder;
    } catch (err) {
      console.error("Error fetching folder metadata:", err);
      folderMetadata = null;
    }
  }

  onDestroy(() => {
    teardownStream();
  });

  let _prevParamsKey = "";

  $effect(() => {
    const space = $params.space_name;
    const sp = $params.subpath;
    if (!space || !sp) return;

    const key = `${space}|${sp}`;
    if (key === _prevParamsKey) return;
    _prevParamsKey = key;

    initializeContent();
  });

  $effect(() => {
    const path = `/${actualSubpath}`;
    if (streamEnabled && spaceName && actualSubpath) {
      setupStream(spaceName, path);
    } else if (streamSubscribedKey) {
      teardownStream();
    }
  });

  let searchTimeout: any;

  function handleSearchInput() {
    if (searchTimeout) clearTimeout(searchTimeout);
    searchTimeout = setTimeout(() => {
      loadContents(true);
    }, 400);
  }

  async function loadContents(reset = false) {
    if (reset) {
      isLoading = true;
      currentOffset = 0;
      allContents = [];
    } else {
      isLoadingMore = true;
    }
    error = null;

    try {
      const response = await getSpaceContents(
        spaceName,
        `/${actualSubpath}`,
        getCurrentScope(),
        itemsPerLoad,
        currentOffset,
        false,
        undefined,
        mergeSearch(searchQuery.trim(), buildHideFoldersSearch(spaceHideFolders))
      );

      totalItemsCount = response?.attributes?.total || 0;

      if (response && response.records) {
        const newItems = await Promise.all(
          response.records.map(async (item: any) => {
            let avatarUrl = "";
            try {
              const result = getAvatar(item.attributes?.owner_shortname);
              avatarUrl = (result instanceof Promise ? await result : result) ?? "";
            } catch {
              avatarUrl = "";
            }
            return { ...item, avatarUrl };
          })
        );

        if (reset) {
          allContents = newItems;
        } else {
          allContents = [...allContents, ...newItems];
        }

        hasMoreServerItems = newItems.length === itemsPerLoad;
        currentOffset += newItems.length;

        extractAvailableTags();
      } else {
        if (reset) {
          allContents = [];
          availableTags = [];
        }
        hasMoreServerItems = false;
      }
    } catch (err) {
      console.error("Error fetching space contents:", err);
      error = "Failed to load space contents";
      if (reset) {
        allContents = [];
        availableTags = [];
      }
      hasMoreServerItems = false;
    } finally {
      isLoading = false;
      isLoadingMore = false;
    }
  }

  function extractAvailableTags() {
    const tagSet = new Set();

    allContents.forEach((item) => {
      if (item.attributes?.tags && Array.isArray(item.attributes?.tags)) {
        item.attributes?.tags.forEach((tag: any) => {
          if (tag && tag.trim() && tag !== "") {
            tagSet.add(tag.trim());
          }
        });
      }
    });

    availableTags = Array.from(tagSet).sort();
  }

  function loadMoreItems() {
    if (isLoadingMore || !hasMoreServerItems) return;
    loadContents(false);
  }

  function handleItemsPerLoadChange(newItemsPerLoad: any) {
    itemsPerLoad = newItemsPerLoad;
    loadContents(true);
  }

  function handleItemClick(item: any) {
    if (item.resource_type === "folder") {
      const newSubpath = `${subpath}-${item.shortname}`;
      $goto("/catalogs/[space_name]/[subpath]", {
        space_name: spaceName,
        subpath: newSubpath,
      });
    } else {
      $goto("/catalogs/[space_name]/[subpath]/[shortname]/[resource_type]", {
        space_name: spaceName,
        subpath: subpath,
        shortname: item.shortname,
        resource_type: item.resource_type,
      });
    }
  }

  // function getItemIcon(item: any) {
  //   switch (item.resource_type) {
  //     case "folder":
  //       return "📁";
  //     case "content":
  //       return "📄";
  //     case "post":
  //       return "📝";
  //     case "ticket":
  //       return "🎫";
  //     case "user":
  //       return "👤";
  //     case "media":
  //       return "🖼️";
  //     default:
  //       return "📋";
  //   }
  // }

  function getDisplayName(item: any) {
    if (item.attributes?.displayname) {
      return (
        item.attributes?.payload?.body?.title ||
        item.attributes.displayname.ar ||
        item.attributes.displayname.en ||
        item.shortname
      );
    }
    return item.attributes?.payload?.body?.title || item.shortname;
  }

  function formatDate(dateString: any) {
    if (!dateString) return "N/A";
    return new Date(dateString).toLocaleDateString();
  }

  function formatRelativeTime(dateString: any) {
    if (!dateString) return "Unknown";
    const date = new Date(dateString);
    const now = new Date();
    const diffInSeconds = Math.floor((now.getTime() - date.getTime()) / 1000);

    if (diffInSeconds < 60) return "Just now";
    if (diffInSeconds < 3600) return `${Math.floor(diffInSeconds / 60)}m ago`;
    if (diffInSeconds < 86400)
      return `${Math.floor(diffInSeconds / 3600)}h ago`;
    if (diffInSeconds < 2592000)
      return `${Math.floor(diffInSeconds / 86400)}d ago`;
    return formatDate(dateString);
  }

  function navigateToBreadcrumb(path: any) {
    if (path) {
      $goto(path);
    }
  }

  function clearFilters() {
    searchQuery = "";
    selectedTags = [];
    sortBy = "name";
    sortOrder = "asc";
    filterType = "all";
    filterStatus = "all";
  }

  function toggleSortOrder() {
    sortOrder = sortOrder === "asc" ? "desc" : "asc";
  }

  function toggleTag(tag: any) {
    if (selectedTags.includes(tag)) {
      selectedTags = selectedTags.filter((t) => t !== tag);
    } else {
      selectedTags = [...selectedTags, tag];
    }
  }

  function removeTag(tag: any) {
    selectedTags = selectedTags.filter((t) => t !== tag);
  }

  function handleCardTagClick(event: any, tag: any) {
    event.stopPropagation();
    toggleTag(tag);
  }

  const filteredContentsDerived = $derived.by(() => {
    let filtered = [...allContents];
    // `hide_folders` is applied server-side via `-@shortname:...` in loadContents.

    if (filterType !== "all") {
      filtered = filtered.filter((item) => item.resource_type === filterType);
    }

    if (filterStatus !== "all") {
      filtered = filtered.filter((item) =>
        filterStatus === "active"
          ? item.attributes?.is_active !== false
          : item.attributes?.is_active === false
      );
    }

    if (selectedTags.length > 0) {
      filtered = filtered.filter((item) => {
        if (!item.attributes?.tags || !Array.isArray(item.attributes?.tags))
          return false;

        return selectedTags.every((selectedTag) =>
          item.attributes.tags.some(
            (itemTag: any) =>
              itemTag &&
              itemTag.trim().toLowerCase() === selectedTag.toLowerCase()
          )
        );
      });
    }

    filtered.sort((a, b) => {
      let aValue, bValue;

      switch (sortBy) {
        case "name":
          aValue = getDisplayName(a).toLowerCase();
          bValue = getDisplayName(b).toLowerCase();
          break;
        case "type":
          aValue = a.resource_type;
          bValue = b.resource_type;
          break;
        case "owner":
          aValue = (a.attributes?.owner_shortname || "").toLowerCase();
          bValue = (b.attributes?.owner_shortname || "").toLowerCase();
          break;
        case "created":
          aValue = new Date(a.attributes?.created_at || 0);
          bValue = new Date(b.attributes?.created_at || 0);
          break;
        default:
          aValue = a.shortname.toLowerCase();
          bValue = b.shortname.toLowerCase();
      }

      let result;
      if (aValue > bValue) result = 1;
      else if (aValue < bValue) result = -1;
      else result = 0;

      return sortOrder === "desc" ? -result : result;
    });

    return filtered;
  });

  const hasMoreItems = $derived(hasMoreServerItems);

  function shareItem(item: any) {
    const url = `${window.location.origin}${withBasePrefix(`/catalogs/${spaceName}/${subpath}/${item.shortname}`)}?resource_type=${item.resource_type}`;
    const title = getDisplayName(item);

    if (navigator.share) {
      navigator
        .share({
          title: title,
          text: `Check out this content: ${title}`,
          url: url,
        })
        .catch(console.error);
    } else {
      navigator.clipboard
        .writeText(url)
        .then(() => {
          alert($_("catalog_contents.share.copied_to_clipboard"));
        })
        .catch(() => {
          prompt($_("catalog_contents.share.copy_link"), url);
        });
    }
  }

  function reportItem(item: any) {
    const reason = prompt($_("catalog_contents.report.reason_prompt"));
    if (reason && reason.trim()) {
      alert($_("catalog_contents.report.submitted"));
    }
  }

  const displayedTags = $derived.by(() => {
    if (showAllTags) return availableTags;
    return availableTags.slice(0, 10);
  });
</script>

<div class="catalog-contents-page" class:rtl={$isRTL}>
  <div class="header-section">
    <div class="container mx-auto px-4 py-6 max-w-7xl">
      <nav
        class="flex mb-4"
        aria-label={$_("catalog_contents.breadcrumb_label")}
      >
        <ol class="inline-flex items-center space-x-1 md:space-x-3">
          {#each breadcrumbs as crumb, index}
            <li class="inline-flex items-center">
              {#if index > 0}
                <svg
                  class="breadcrumb-separator"
                  fill="currentColor"
                  viewBox="0 0 20 20"
                >
                  <path
                    fill-rule="evenodd"
                    d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"
                    clip-rule="evenodd"
                  ></path>
                </svg>
              {/if}
              {#if crumb.path}
                <button
                  onclick={() => navigateToBreadcrumb(crumb.path)}
                  class="breadcrumb-link"
                  aria-label={crumb.name}
                >
                  {crumb.name}
                </button>
              {:else}
                <span class="breadcrumb-current">
                  {crumb.name}
                </span>
              {/if}
            </li>
          {/each}
        </ol>
      </nav>

      <div class="header-content">
        <div class="header-info">
          <h1 class="page-title">
            {breadcrumbs[breadcrumbs.length - 1]?.name ||
              actualSubpath.split("/").pop()}
          </h1>
          <p class="page-description">
            {$_("catalog_contents.browse_contents")}
            <span class="space-name">{spaceName}</span>
            {#if actualSubpath !== ""}
              / <span class="subpath-name">{actualSubpath}</span>
            {/if}
          </p>
        </div>
        <div class="header-actions">
          {#if canUploadCSV}
            <button
              aria-label="Upload CSV"
              onclick={() => isCSVUploadModalOpen = true}
              class="csv-button csv-button-upload"
            >
              <UploadOutline class="w-4 h-4" />
              Import CSV
            </button>
          {/if}
          {#if canDownloadCSV}
            <button
              aria-label="Download CSV"
              onclick={() => isCSVDownloadModalOpen = true}
              class="csv-button csv-button-download"
            >
              <DownloadOutline class="w-4 h-4" />
              Export CSV
            </button>
          {/if}
        </div>
      </div>
    </div>
  </div>

  <div class="container mx-auto px-4 py-8 max-w-7xl">
    {#if isLoading}
      <div class="loading-state">
        <div class="spinner spinner-lg"></div>
        <p class="loading-text">{$_("catalog_contents.loading")}</p>
      </div>
    {:else if error}
      <div class="error-state">
        <div class="error-icon">
          <svg
            class="w-12 h-12 text-red-500"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            ></path>
          </svg>
        </div>
        <h3 class="error-title">
          {$_("catalog_contents.error.title")}
        </h3>
        <p class="error-message">{error}</p>
        <button
          aria-label={$_("route_labels.aria_retry_loading_content")}
          onclick={() => loadContents()}
          class="retry-button"
        >
          {$_("catalog_contents.error.try_again")}
        </button>
      </div>
    {:else}
      {#if availableTags.length > 0}
        <div class="tags-filter-section">
          <div class="tags-header">
            <h3 class="tags-title">
              {$_("catalog_contents.tags.available_tags")}
            </h3>
            {#if availableTags.length > 10}
              <button
                aria-label={$_("route_labels.aria_show_all_tags")}
                onclick={() => (showAllTags = !showAllTags)}
                class="show-all-tags-button"
              >
                {showAllTags
                  ? $_("catalog_contents.tags.show_less")
                  : $_("catalog_contents.tags.show_all")}
                ({availableTags.length})
              </button>
            {/if}
          </div>

          <div class="tags-container">
            {#each displayedTags as tag}
              <button
                aria-label={`Filter by tag: ${tag}`}
                onclick={() => toggleTag(tag)}
                class="tag-filter-button {selectedTags.includes(tag)
                  ? 'tag-selected'
                  : 'tag-unselected'}"
              >
                #{tag}
                {#if selectedTags.includes(tag)}
                  <svg
                    class="tag-check-icon"
                    fill="currentColor"
                    viewBox="0 0 20 20"
                  >
                    <path
                      fill-rule="evenodd"
                      d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                      clip-rule="evenodd"
                    ></path>
                  </svg>
                {/if}
              </button>
            {/each}
          </div>

          {#if selectedTags.length > 0}
            <div class="selected-tags-section">
              <div class="selected-tags-header">
                <span class="selected-tags-title"
                  >{$_("catalog_contents.tags.selected_tags")}:</span
                >
                <button
                  aria-label={$_("route_labels.aria_clear_all_selected_tags")}
                  onclick={() => {
                    selectedTags = [];
                  }}
                  class="clear-tags-button"
                >
                  {$_("catalog_contents.tags.clear_all")}
                </button>
              </div>
              <div class="selected-tags-container">
                {#each selectedTags as tag}
                  <div class="selected-tag">
                    #{tag}
                    <button
                      onclick={() => removeTag(tag)}
                      class="remove-tag-button"
                      aria-label={$_("catalog_contents.tags.remove_tag", {
                        values: { tag },
                      })}
                    >
                      <svg
                        class="remove-tag-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M6 18L18 6M6 6l12 12"
                        ></path>
                      </svg>
                    </button>
                  </div>
                {/each}
              </div>
            </div>
          {/if}
        </div>
      {/if}

      {#if totalItemsCount === 0}
        <div class="empty-state">
          <div class="empty-icon">
            <svg
              class="w-12 h-12 text-gray-400"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                stroke-linecap="round"
                stroke-linejoin="round"
                stroke-width="2"
                d="M9 13h6m-3-3v6m5 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
              ></path>
            </svg>
          </div>
          <h3 class="empty-title">
            {$_("catalog_contents.empty.title")}
          </h3>
          <p class="empty-message">
            {searchQuery || selectedTags.length > 0
              ? $_("catalog_contents.empty.no_matches")
              : $_("catalog_contents.empty.folder_empty")}
          </p>
          {#if searchQuery || selectedTags.length > 0}
            <button
              aria-label={$_("route_labels.aria_clear_all_filters")}
              onclick={clearFilters}
              class="clear-filters-button"
            >
              {$_("catalog_contents.filters.clear_all")}
            </button>
          {/if}
        </div>
      {:else}
        <div class="search-filter-section">
          <!-- Compact search row: search + sort + expand button -->
          <div class="search-compact-row">
            <div class="search-input-wrapper">
              <svg
                class="search-icon"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                ></path>
              </svg>
              <label for="search-input"></label>
              <input
                type="text"
                bind:value={searchQuery}
                placeholder={$_("catalog_contents.search.placeholder")}
                class="search-input"
                aria-label={$_("catalog_contents.search.label")}
                oninput={handleSearchInput}
              />
              {#if searchQuery}
                <button
                  onclick={() => {
                    searchQuery = "";
                    loadContents(true);
                  }}
                  class="clear-search-button"
                  aria-label={$_("catalog_contents.search.clear")}
                >
                  <svg
                    class="w-4 h-4"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M6 18L18 6M6 6l12 12"
                    ></path>
                  </svg>
                </button>
              {/if}
            </div>

            <div class="sort-inline">
              <select
                id="sort-by-select"
                bind:value={sortBy}
                class="filter-select sort-select"
                title={$_("catalog_contents.filters.sort_by")}
                aria-label={$_("catalog_contents.filters.sort_by")}
              >
                {#each sortOptions as option}
                  <option value={option.value}>{option.label}</option>
                {/each}
              </select>
              <button
                onclick={toggleSortOrder}
                class="sort-order-button"
                title={$_("catalog_contents.filters.toggle_sort")}
                aria-label={$_("catalog_contents.filters.toggle_sort")}
              >
                <svg
                  class="w-4 h-4"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  {#if sortOrder === "asc"}
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M3 4h13M3 8h9m-9 4h6m4 0l4-4m0 0l4 4m-4-4v12"
                    ></path>
                  {:else}
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M3 4h13M3 8h9m-9 4h9m5-4v12m0 0l-4-4m4 4l-4-4"
                    ></path>
                  {/if}
                </svg>
              </button>
            </div>

            <button
              onclick={() => (showFilters = !showFilters)}
              class="expand-filters-button"
              class:filters-active={showFilters || filterType !== "all" || filterStatus !== "all"}
              title={showFilters ? $_("catalog_contents.filters.collapse_filters") : $_("catalog_contents.filters.expand_filters")}
              aria-label={showFilters ? $_("catalog_contents.filters.collapse_filters") : $_("catalog_contents.filters.expand_filters")}
            >
              <svg
                class="w-4 h-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z"
                ></path>
              </svg>
              <svg
                class="expand-chevron"
                class:expand-chevron-open={showFilters}
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  stroke-linecap="round"
                  stroke-linejoin="round"
                  stroke-width="2"
                  d="M19 9l-7 7-7-7"
                ></path>
              </svg>
            </button>

            {#if searchQuery || selectedTags.length > 0 || filterType !== "all" || filterStatus !== "all"}
              <button
                title={$_("catalog_contents.filters.clear_all")}
                aria-label={$_("catalog_contents.filters.clear_all")}
                onclick={clearFilters}
                class="clear-all-inline-button"
              >
                <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"></path>
                </svg>
              </button>
            {/if}
          </div>

          <!-- Collapsible filters panel -->
          {#if showFilters}
            <div class="collapsible-filters">
              <div class="filter-group">
                <label class="filter-label" for="type-filter-select">{$_("catalog_contents.filters.type")}</label>
                <select
                  id="type-filter-select"
                  bind:value={filterType}
                  class="filter-select"
                  title={$_("catalog_contents.filters.type")}
                  aria-label={$_("catalog_contents.filters.type")}
                >
                  <option value="all">{$_("catalog_contents.filters.all_types")}</option>
                  {#each getResourceTypes() as type}
                    <option value={type}>{type}</option>
                  {/each}
                </select>
              </div>

              <div class="filter-group">
                <label class="filter-label" for="status-filter-select">{$_("catalog_contents.filters.status")}</label>
                <select
                  id="status-filter-select"
                  bind:value={filterStatus}
                  class="filter-select"
                  title={$_("catalog_contents.filters.status")}
                  aria-label={$_("catalog_contents.filters.status")}
                >
                  <option value="all">{$_("catalog_contents.filters.all_statuses")}</option>
                  <option value="active">{$_("catalog_contents.filters.active")}</option>
                  <option value="inactive">{$_("catalog_contents.filters.inactive")}</option>
                </select>
              </div>

              <div class="filter-group">
                <label class="filter-label" for="items-per-load-select">{$_("catalog_contents.infinite_scroll.items_per_load")}</label>
                <select
                  id="items-per-load-select"
                  bind:value={itemsPerLoad}
                  onchange={(e) => handleItemsPerLoadChange(parseInt((e.target as HTMLSelectElement).value))}
                  class="filter-select"
                  title={$_("catalog_contents.infinite_scroll.items_per_load")}
                  aria-label={$_("catalog_contents.infinite_scroll.items_per_load")}
                >
                  {#each itemsPerLoadOptions as option}
                    <option value={option}>{option}</option>
                  {/each}
                </select>
              </div>
            </div>
          {/if}

          <div class="results-summary">
            <div class="results-info">
              {$_("catalog_contents.infinite_scroll.showing_items", {
                values: {
                  displayed: filteredContentsDerived.length,
                  total: totalItemsCount,
                },
              })}
              {#if searchQuery}
                {$_("catalog_contents.results.for_query", {
                  values: { query: searchQuery },
                })}
              {/if}
              {#if selectedTags.length > 0}
                {$_("catalog_contents.results.with_tags", {
                  values: { count: selectedTags.length },
                })}
              {/if}
            </div>
          </div>
        </div>

        <div class="card-list-container">
          <div class="card-list">
            {#each filteredContentsDerived as item, index}
              <div
                class="content-card"
                onclick={() => handleItemClick(item)}
                role="button"
                tabindex="0"
                onkeydown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    handleItemClick(item);
                  }
                }}
              >
                <div class="card-avatar">
                  {#if item.attributes?.owner_shortname}
                    {#await getAvatar(item.attributes?.owner_shortname) then avatar}
                      <Avatar
                        src={avatar ?? ""}
                        size="40"
                        alt={item.attributes?.owner_shortname ?? ""}
                      />
                    {/await}
                    <div class="avatar-fallback">
                      {item.attributes?.owner_shortname.charAt(0).toUpperCase()}
                    </div>
                  {:else}
                    <div class="avatar-unknown">
                      <span class="text-sm font-medium text-gray-600">?</span>
                    </div>
                  {/if}
                </div>

                <div class="card-content">
                  <div class="card-header">
                    <h3 class="card-title">
                      {getDisplayName(item)}
                    </h3>
                  </div>

                  <div class="card-meta">
                    <span class="meta-text"
                      >{$_("catalog_contents.card.posted_by")}</span
                    >
                    <span class="meta-author">
                      {item.attributes?.owner_shortname || $_("common.unknown")}
                    </span>
                    <span class="meta-separator">•</span>
                    <span class="meta-time">
                      {formatRelativeTime(item.attributes?.created_at)}
                    </span>
                  </div>

                  {#if item.attributes?.payload?.body?.content || (typeof item.attributes?.payload?.body === "string" && item.attributes?.payload?.body)}
                    {@const content =
                      item.attributes?.payload?.body?.content ||
                      (typeof item.attributes?.payload?.body === "string"
                        ? item.attributes?.payload?.body
                        : "")}
                    {@const isLongContent = content.length > 150}

                    <div class="card-preview">
                      {#if item.attributes?.payload?.content_type === "html"}
                        <div class="prose max-w-none preview-text">
                          {@html isLongContent
                            ? content.substring(0, 150) + "..."
                            : content}
                        </div>
                      {:else if item.attributes?.payload?.content_type === "json"}
                        {@const jsonContent = JSON.stringify(
                          item.attributes.payload.body?.content ||
                            item.attributes.payload.body,
                          null,
                          2
                        )}
                        {@const isLongJson = jsonContent.length > 150}
                        <pre
                          class="bg-gray-50 p-2 rounded text-xs overflow-x-auto preview-text">{isLongJson
                            ? jsonContent.substring(0, 150) + "..."
                            : jsonContent}</pre>
                      {:else}
                        <div class="bg-gray-50 p-2 rounded">
                          <pre
                            class="text-xs whitespace-pre-wrap preview-text">{isLongContent
                              ? content.substring(0, 150) + "..."
                              : content}</pre>
                        </div>
                      {/if}

                      {#if isLongContent || (item.attributes?.payload?.content_type === "json" && JSON.stringify(item.attributes.payload.body?.content || item.attributes.payload.body, null, 2).length > 150)}
                        <button
                          aria-label={`Read more about ${getDisplayName(item)}`}
                          onclick={() => handleItemClick(item)}
                          class="read-more-button"
                        >
                          {$_("catalog_contents.card.read_more")}
                        </button>
                      {/if}
                    </div>
                  {/if}

                  {#if item.attributes?.tags && item.attributes?.tags.length > 0 && item.attributes?.tags[0] !== ""}
                    <div class="card-tags">
                      {#each item.attributes?.tags.slice(0, 3) as tag}
                        {#if tag && tag.trim()}
                          <button
                            aria-label={`Filter by tag: ${tag}`}
                            class="card-tag {selectedTags.includes(tag)
                              ? 'card-tag-selected'
                              : ''}"
                            onclick={(e) => handleCardTagClick(e, tag)}
                          >
                            #{tag}
                          </button>
                        {/if}
                      {/each}
                      {#if item.attributes?.tags.length > 3}
                        <span class="card-tag-more"
                          >+{item.attributes?.tags.length - 3} more</span
                        >
                      {/if}
                    </div>
                  {/if}
                </div>

                <div class="card-stats">
                  <div class="stats-left">
                    <div class="stat-item">
                      <svg
                        class="stat-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z"
                        ></path>
                      </svg>
                      <span class="stat-number">
                        {item.attachments?.comment?.length || 0}
                      </span>
                    </div>
                    <div class="stat-item stat-reactions">
                      <svg
                        class="stat-icon"
                        fill="currentColor"
                        viewBox="0 0 20 20"
                      >
                        <path
                          fill-rule="evenodd"
                          d="M3.172 5.172a4 4 0 015.656 0L10 6.343l1.172-1.171a4 4 0 115.656 5.656L10 17.657l-6.828-6.829a4 4 0 010-5.656z"
                          clip-rule="evenodd"
                        ></path>
                      </svg>
                      <span class="stat-number"
                        >{item.attachments?.reaction?.length || 0}</span
                      >
                    </div>
                    <div class="stat-item stat-media">
                      <svg
                        class="stat-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M15.172 7l-6.586 6.586a2 2 0 102.828 2.828l6.586-6.586a2 2 0 000-2.828z"
                        ></path>
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M9 12l2 2 4-4"
                        ></path>
                      </svg>
                      <span class="stat-number">
                        {item.attachments?.media?.length || 0}
                      </span>
                    </div>
                  </div>

                  <div class="stats-right">
                    <button
                      aria-label={`Share ${getDisplayName(item)}`}
                      class="action-button share-button"
                      onclick={(e) => {
                        e.stopPropagation();
                        shareItem(item);
                      }}
                      title={$_("catalog_contents.card.share")}
                    >
                      <svg
                        class="action-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M8.684 13.342C8.886 12.938 9 12.482 9 12c0-.482-.114-.938-.316-1.342m0 2.684a3 3 0 110-2.684m0 2.684l6.632 3.316m-6.632-6l6.632-3.316m0 0a3 3 0 105.367-2.684 3 3 0 00-5.367 2.684zm0 9.316a3 3 0 105.367 2.684 3 3 0 00-5.367 2.684z"
                        ></path>
                      </svg>
                    </button>

                    <button
                      aria-label={`Report ${getDisplayName(item)}`}
                      class="action-button report-button"
                      onclick={(e) => {
                        e.stopPropagation();
                        reportItem(item);
                      }}
                      title={$_("catalog_contents.card.report")}
                    >
                      <svg
                        class="action-icon"
                        fill="none"
                        stroke="currentColor"
                        viewBox="0 0 24 24"
                      >
                        <path
                          stroke-linecap="round"
                          stroke-linejoin="round"
                          stroke-width="2"
                          d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z"
                        ></path>
                      </svg>
                    </button>
                  </div>
                </div>
              </div>
            {/each}
          </div>

          {#if hasMoreItems}
            <div class="load-more-section">
              <div class="load-more-info">
                <span class="load-more-text">
                  {$_("catalog_contents.infinite_scroll.showing_of", {
                    values: {
                      displayed: filteredContentsDerived.length,
                      total: totalItemsCount,
                    },
                  })}
                </span>
              </div>
              <button
                onclick={loadMoreItems}
                disabled={isLoadingMore}
                class="load-more-button"
                aria-label={$_("catalog_contents.infinite_scroll.load_more")}
              >
                {#if isLoadingMore}
                  <div class="load-more-spinner">
                    <div class="spinner spinner-sm spinner-white"></div>
                  </div>
                  {$_("catalog_contents.infinite_scroll.loading")}
                {:else}
                  <svg
                    class="load-more-icon"
                    fill="none"
                    stroke="currentColor"
                    viewBox="0 0 24 24"
                  >
                    <path
                      stroke-linecap="round"
                      stroke-linejoin="round"
                      stroke-width="2"
                      d="M19 14l-7 7m0 0l-7-7m7 7V3"
                    ></path>
                  </svg>
                  {$_("catalog_contents.infinite_scroll.load_more")}
                {/if}
              </button>
            </div>
          {:else if filteredContentsDerived.length > 0}
            <div class="end-of-results">
              <div class="end-of-results-icon">
                <svg
                  class="w-8 h-8 text-gray-400"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
                  ></path>
                </svg>
              </div>
              <p class="end-of-results-text">
                {$_("catalog_contents.infinite_scroll.end_of_results")}
              </p>
              <p class="end-of-results-count">
                {$_("catalog_contents.infinite_scroll.total_items", {
                  values: { count: totalItemsCount },
                })}
              </p>
            </div>
          {/if}
        </div>
      {/if}
    {/if}
  </div>
</div>

<!-- CSV Import/Export Modals -->
<ModalCSVUpload 
  space_name={spaceName}
  subpath={actualSubpath || "/"} 
  bind:isOpen={isCSVUploadModalOpen}
  onUploadSuccess={loadContents}
/>

<ModalCSVDownload 
  space_name={spaceName}
  subpath={actualSubpath || "/"} 
  bind:isOpen={isCSVDownloadModalOpen}
/>

<style>
  .catalog-contents-page {
    min-height: 100vh;
    background: var(--gradient-page);
  }

  .rtl {
    direction: rtl;
  }

  /* Header Section */
  .header-section {
    background: rgba(255, 255, 255, 0.95);
    backdrop-filter: blur(12px);
    border-bottom: 1px solid rgba(148, 163, 184, 0.2);
    position: sticky;
    top: 0;
    z-index: 10;
    box-shadow: var(--shadow-sm);
  }

  .header-content {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .rtl .header-content {
    text-align: right;
  }

  .header-info {
    flex: 1;
  }

  .header-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    align-items: center;
  }

  .csv-button {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.625rem 1rem;
    font-size: 0.875rem;
    font-weight: 600;
    border-radius: var(--radius-md);
    border: 1px solid var(--color-gray-200);
    background-color: var(--surface-card);
    color: var(--color-gray-700);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-xs);
  }

  .csv-button:hover {
    background-color: var(--color-gray-50);
    border-color: var(--color-gray-300);
    transform: translateY(-1px);
    box-shadow: var(--shadow-md);
  }

  .csv-button-upload {
    color: #059669;
    border-color: var(--color-success);
  }

  .csv-button-upload:hover {
    background-color: #ecfdf5;
    border-color: #059669;
  }

  .csv-button-download {
    color: var(--color-primary-600);
    border-color: var(--color-info);
  }

  .csv-button-download:hover {
    background-color: var(--color-primary-50);
    border-color: var(--color-primary-600);
  }

  .page-title {
    font-size: 1.875rem;
    font-weight: 700;
    color: var(--color-gray-800);
    margin-bottom: 0.5rem;
    line-height: 1.2;
  }

  .page-description {
    color: #64748b;
    font-size: 1.125rem;
  }

  .space-name {
    font-weight: 600;
    color: var(--color-primary-600);
  }

  .subpath-name {
    font-weight: 500;
  }

  /* Breadcrumb Styles */
  .breadcrumb-separator {
    width: 1rem;
    height: 1rem;
    color: var(--color-gray-400);
    margin: 0 0.5rem;
  }

  .rtl .breadcrumb-separator {
    transform: rotate(180deg);
  }

  .breadcrumb-link {
    color: #64748b;
    font-size: 0.875rem;
    font-weight: 500;
    transition: all var(--duration-normal) var(--ease-out);
    padding: 0.25rem 0.5rem;
    border-radius: var(--radius-sm);
    border: none;
    background: none;
    cursor: pointer;
  }

  .breadcrumb-link:hover {
    color: var(--color-primary-600);
    background-color: rgba(59, 130, 246, 0.1);
    text-decoration: underline;
  }

  .breadcrumb-current {
    color: var(--color-gray-800);
    font-weight: 600;
    font-size: 0.875rem;
    background: linear-gradient(135deg, #dbeafe 0%, var(--color-primary-100) 100%);
    padding: 0.5rem 0.75rem;
    border-radius: var(--radius-full);
    border: 1px solid rgba(59, 130, 246, 0.2);
  }

  /* Tags Filter Section */
  .tags-filter-section {
    background: rgba(255, 255, 255, 0.9);
    backdrop-filter: blur(8px);
    border-radius: var(--radius-lg);
    border: 1px solid rgba(148, 163, 184, 0.2);
    padding: 1.5rem;
    margin-bottom: 2rem;
    box-shadow: var(--shadow-sm);
  }

  .tags-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 1rem;
  }

  .tags-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-gray-800);
  }

  .show-all-tags-button {
    font-size: 0.875rem;
    color: var(--color-primary-600);
    background: none;
    border: none;
    cursor: pointer;
    font-weight: 500;
    transition: color var(--duration-normal) var(--ease-out);
  }

  .show-all-tags-button:hover {
    color: var(--color-primary-600);
    text-decoration: underline;
  }

  .tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
    margin-bottom: 1rem;
  }

  .tag-filter-button {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.5rem 0.75rem;
    border-radius: var(--radius-full);
    font-size: 0.875rem;
    font-weight: 500;
    border: 1px solid;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .tag-unselected {
    background: rgba(255, 255, 255, 0.8);
    color: #64748b;
    border-color: rgba(148, 163, 184, 0.3);
  }

  .tag-unselected:hover {
    background: rgba(59, 130, 246, 0.1);
    color: var(--color-primary-600);
    border-color: rgba(59, 130, 246, 0.3);
  }

  .tag-selected {
    background: linear-gradient(135deg, var(--color-primary-600) 0%, var(--color-primary-600) 100%);
    color: white;
    border-color: var(--color-primary-600);
    box-shadow: var(--shadow-sm);
  }

  .tag-check-icon {
    width: 1rem;
    height: 1rem;
  }

  .selected-tags-section {
    border-top: 1px solid rgba(148, 163, 184, 0.2);
    padding-top: 1rem;
  }

  .selected-tags-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 0.75rem;
  }

  .selected-tags-title {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-gray-700);
  }

  .clear-tags-button {
    font-size: 0.75rem;
    color: var(--color-error);
    background: none;
    border: none;
    cursor: pointer;
    font-weight: 500;
    transition: color var(--duration-normal) var(--ease-out);
  }

  .clear-tags-button:hover {
    color: #dc2626;
    text-decoration: underline;
  }

  .selected-tags-container {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .selected-tag {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.375rem 0.75rem;
    background: linear-gradient(135deg, #dbeafe 0%, var(--color-primary-100) 100%);
    color: #1e40af;
    border-radius: var(--radius-full);
    font-size: 0.875rem;
    font-weight: 500;
    border: 1px solid rgba(59, 130, 246, 0.2);
  }

  .remove-tag-button {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 1rem;
    height: 1rem;
    background: none;
    border: none;
    cursor: pointer;
    color: #64748b;
    transition: color var(--duration-normal) var(--ease-out);
  }

  .remove-tag-button:hover {
    color: var(--color-error);
  }

  .remove-tag-icon {
    width: 0.75rem;
    height: 0.75rem;
  }

  /* State Components */
  .loading-state,
  .error-state,
  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 5rem 0;
    text-align: center;
  }

  .loading-text {
    margin-top: 1rem;
    color: #64748b;
    font-size: 1.125rem;
    font-weight: 500;
  }

  .error-icon,
  .empty-icon {
    width: 6rem;
    height: 6rem;
    background: linear-gradient(135deg, #fee2e2 0%, #fecaca 100%);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 1.5rem;
    border: 1px solid rgba(239, 68, 68, 0.2);
  }

  .empty-icon {
    background: linear-gradient(135deg, var(--color-gray-100) 0%, var(--color-gray-200) 100%);
    border: 1px solid rgba(107, 114, 128, 0.2);
  }

  .error-title,
  .empty-title {
    font-size: 1.5rem;
    font-weight: 600;
    color: var(--color-gray-800);
    margin-bottom: 0.5rem;
  }

  .error-message,
  .empty-message {
    color: #64748b;
    font-size: 1.125rem;
    margin-bottom: 1.5rem;
  }

  .retry-button,
  .clear-filters-button {
    padding: 0.75rem 1.5rem;
    background: linear-gradient(135deg, var(--color-primary-600) 0%, var(--color-primary-600) 100%);
    color: white;
    border-radius: var(--radius-md);
    font-weight: 500;
    border: 1px solid rgba(37, 99, 235, 0.3);
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-sm);
    cursor: pointer;
  }

  .retry-button:hover,
  .clear-filters-button:hover {
    background: linear-gradient(135deg, var(--color-primary-600) 0%, #1e40af 100%);
    box-shadow: var(--shadow-md);
  }

  .search-filter-section {
    background: rgba(255, 255, 255, 0.9);
    backdrop-filter: blur(8px);
    border-radius: var(--radius-lg);
    border: 1px solid rgba(148, 163, 184, 0.2);
    padding: 1rem 1.5rem;
    margin-bottom: 2rem;
    box-shadow: var(--shadow-sm);
  }

  .search-compact-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .sort-inline {
    display: flex;
    gap: 0.375rem;
    flex-shrink: 0;
  }

  .rtl .sort-inline {
    flex-direction: row-reverse;
  }

  .expand-filters-button {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.625rem;
    border: 1px solid rgba(209, 213, 219, 0.8);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.95);
    color: var(--color-gray-500);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    flex-shrink: 0;
  }

  .expand-filters-button:hover {
    background: rgba(249, 250, 251, 0.95);
    color: var(--color-gray-700);
    border-color: var(--color-gray-400);
  }

  .expand-filters-button.filters-active {
    color: var(--color-primary-600);
    border-color: #93c5fd;
    background: var(--color-primary-50);
  }

  .expand-chevron {
    width: 0.875rem;
    height: 0.875rem;
    transition: transform var(--duration-normal) var(--ease-out);
  }

  .expand-chevron-open {
    transform: rotate(180deg);
  }

  .clear-all-inline-button {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0.625rem;
    border: 1px solid rgba(239, 68, 68, 0.3);
    border-radius: var(--radius-md);
    background: rgba(254, 242, 242, 0.8);
    color: var(--color-error);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    flex-shrink: 0;
  }

  .clear-all-inline-button:hover {
    background: #fee2e2;
    border-color: var(--color-error);
  }

  .collapsible-filters {
    display: flex;
    flex-wrap: wrap;
    gap: 1rem;
    align-items: end;
    padding-top: 0.75rem;
    margin-top: 0.75rem;
    border-top: 1px solid rgba(148, 163, 184, 0.15);
  }

  .search-input-wrapper {
    position: relative;
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
  }

  .search-icon {
    position: absolute;
    width: 1.25rem;
    height: 1.25rem;
    color: var(--color-gray-400);
    z-index: 1;
    left: 0.75rem;
  }

  .rtl .search-icon {
    left: auto;
    right: 0.75rem;
  }

  .search-input {
    width: 100%;
    padding: 0.75rem 1rem 0.75rem 2.75rem;
    border: 1px solid rgba(209, 213, 219, 0.8);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.95);
    font-size: 0.875rem;
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-xs);
  }

  .rtl .search-input {
    padding: 0.75rem 2.75rem 0.75rem 1rem;
    text-align: right;
  }

  .search-input:focus {
    outline: none;
    border-color: var(--color-primary-600);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .clear-search-button {
    position: absolute;
    color: var(--color-gray-400);
    transition: color var(--duration-normal) var(--ease-out);
    border: none;
    background: none;
    cursor: pointer;
    padding: 0.25rem;
    border-radius: 0.25rem;
    right: 0.75rem;
  }

  .rtl .clear-search-button {
    right: auto;
    left: 0.75rem;
  }

  .clear-search-button:hover {
    color: var(--color-gray-500);
    background-color: rgba(107, 114, 128, 0.1);
  }

  .filter-group {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    min-width: 140px;
  }

  .filter-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-gray-700);
  }

  .rtl .filter-label {
    text-align: right;
  }

  .filter-select {
    padding: 0.75rem 1rem;
    border: 1px solid rgba(209, 213, 219, 0.8);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.95);
    font-size: 0.875rem;
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-xs);
  }

  .rtl .filter-select {
    text-align: right;
  }

  .filter-select:focus {
    outline: none;
    border-color: var(--color-primary-600);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  .sort-select {
    flex: 1;
  }

  .sort-order-button {
    padding: 0.75rem;
    border: 1px solid rgba(209, 213, 219, 0.8);
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.95);
    color: var(--color-gray-500);
    transition: all var(--duration-normal) var(--ease-out);
    cursor: pointer;
    box-shadow: var(--shadow-xs);
  }

  .sort-order-button:hover {
    background: rgba(249, 250, 251, 0.95);
    color: var(--color-gray-700);
  }

  .sort-order-button:focus {
    outline: none;
    border-color: var(--color-primary-600);
    box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.1);
  }

  /* Results Summary */
  .results-summary {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding-top: 1rem;
    border-top: 1px solid rgba(148, 163, 184, 0.2);
    margin-top: 1.5rem;
  }

  .results-info {
    font-size: 0.875rem;
    color: #64748b;
  }

  .card-list-container {
    background: rgba(255, 255, 255, 0.9);
    backdrop-filter: blur(8px);
    border-radius: var(--radius-lg);
    border: 1px solid rgba(148, 163, 184, 0.2);
    box-shadow: var(--shadow-md);
    overflow: hidden;
  }

  .card-list > :not(:last-child) {
    border-bottom: 1px solid rgba(148, 163, 184, 0.1);
  }

  .content-card {
    display: flex;
    align-items: flex-start;
    gap: 1rem;
    padding: 1rem;
    transition: all var(--duration-normal) var(--ease-out);
    cursor: pointer;
    border-bottom: 1px solid rgba(148, 163, 184, 0.1);
  }

  .content-card:last-child {
    border-bottom: none;
  }

  .content-card:hover {
    background: rgba(59, 130, 246, 0.02);
    transform: translateY(-1px);
    box-shadow: var(--shadow-md);
  }

  .card-avatar {
    flex-shrink: 0;
    position: relative;
  }

  .avatar-fallback {
    width: 3rem;
    height: 3rem;
    background: linear-gradient(135deg, var(--color-gray-500) 0%, var(--color-gray-600) 100%);
    border-radius: 50%;
    display: none;
    align-items: center;
    justify-content: center;
    color: white;
    font-weight: 600;
    font-size: 1.125rem;
    border: 2px solid rgba(255, 255, 255, 0.8);
    box-shadow: var(--shadow-sm);
  }

  .avatar-unknown {
    width: 3rem;
    height: 3rem;
    background: linear-gradient(135deg, var(--color-gray-200) 0%, var(--color-gray-300) 100%);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 2px solid rgba(255, 255, 255, 0.8);
    box-shadow: var(--shadow-sm);
  }

  /* Content Section */
  .card-content {
    flex: 1;
    min-width: 0;
  }

  .rtl .card-content {
    text-align: right;
  }

  .card-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 0.5rem;
  }

  .card-title {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-gray-800);
    line-height: 1.4;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    transition: color var(--duration-normal) var(--ease-out);
    flex: 1;
  }

  .content-card:hover .card-title {
    color: var(--color-primary-600);
  }

  .card-meta {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.875rem;
    color: #64748b;
    margin-bottom: 0.75rem;
    flex-wrap: wrap;
  }

  .meta-text {
    color: var(--color-gray-400);
  }

  .meta-author {
    font-weight: 500;
    color: var(--color-gray-700);
  }

  .meta-separator {
    color: var(--color-gray-300);
  }

  .meta-time {
    color: #64748b;
  }

  /* Card Preview Styles */
  .card-preview {
    margin-bottom: 0.75rem;
  }

  .preview-text {
    font-size: 0.875rem;
    color: #64748b;
    line-height: 1.5;
    margin-bottom: 0.5rem;
    display: -webkit-box;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .read-more-button {
    font-size: 0.75rem;
    color: var(--color-primary-600);
    background: none;
    border: none;
    cursor: pointer;
    font-weight: 500;
    transition: color var(--duration-normal) var(--ease-out);
  }

  .read-more-button:hover {
    color: var(--color-primary-600);
    text-decoration: underline;
  }

  .card-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.375rem;
    margin-bottom: 0.75rem;
  }

  .card-tag {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.5rem;
    border-radius: var(--radius-full);
    font-size: 0.625rem;
    font-weight: 500;
    background: var(--color-primary-100);
    color: #3730a3;
    transition: all var(--duration-normal) var(--ease-out);
    border: 1px solid transparent;
    cursor: pointer;
  }

  .card-tag:hover {
    background: var(--color-primary-200);
    transform: translateY(-1px);
  }

  .card-tag-selected {
    background: linear-gradient(135deg, var(--color-primary-600) 0%, var(--color-primary-600) 100%);
    color: white;
    border-color: var(--color-primary-600);
    box-shadow: var(--shadow-sm);
  }

  .card-tag-more {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.5rem;
    border-radius: var(--radius-full);
    font-size: 0.625rem;
    font-weight: 500;
    background: #f1f5f9;
    color: #64748b;
  }

  /* Stats Section */
  .card-stats {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-shrink: 0;
  }

  .stats-left {
    display: flex;
    gap: 0.75rem;
  }

  .stats-right {
    display: flex;
    gap: 0.5rem;
  }

  .stat-item {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    font-size: 0.875rem;
    color: #64748b;
    padding: 0.375rem 0.75rem;
    background: rgba(248, 250, 252, 0.8);
    border-radius: var(--radius-md);
    border: 1px solid rgba(148, 163, 175, 0.2);
  }

  .stat-reactions {
    color: var(--color-error);
    background: rgba(254, 242, 242, 0.8);
    border-color: rgba(254, 202, 202, 0.5);
  }

  .stat-media {
    color: #8b5cf6;
    background: rgba(245, 243, 255, 0.8);
    border-color: rgba(196, 181, 253, 0.5);
  }

  .stat-icon {
    width: 1rem;
    height: 1rem;
    flex-shrink: 0;
  }

  .stat-number {
    font-weight: 600;
    min-width: 1.5rem;
    text-align: center;
  }

  .action-button {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2rem;
    height: 2rem;
    border-radius: var(--radius-sm);
    border: 1px solid rgba(148, 163, 175, 0.3);
    background: rgba(255, 255, 255, 0.8);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .action-icon {
    width: 1rem;
    height: 1rem;
  }

  .share-button:hover {
    background: rgba(59, 130, 246, 0.1);
    border-color: var(--color-info);
    color: var(--color-info);
  }

  .report-button:hover {
    background: rgba(239, 68, 68, 0.1);
    border-color: var(--color-error);
    color: var(--color-error);
  }

  /* Load More Section */
  .load-more-section {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1rem;
    padding: 2rem;
    border-top: 1px solid rgba(148, 163, 184, 0.2);
    background: linear-gradient(135deg, var(--surface-page) 0%, #f1f5f9 100%);
  }

  .load-more-info {
    text-align: center;
  }

  .load-more-text {
    font-size: 0.875rem;
    color: #64748b;
    font-weight: 500;
  }

  .load-more-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.875rem 2rem;
    background: linear-gradient(135deg, var(--color-primary-600) 0%, var(--color-primary-600) 100%);
    color: white;
    border-radius: var(--radius-md);
    font-weight: 600;
    font-size: 0.875rem;
    border: 1px solid rgba(37, 99, 235, 0.3);
    transition: all var(--duration-normal) var(--ease-out);
    box-shadow: var(--shadow-sm);
    cursor: pointer;
    min-width: 140px;
    justify-content: center;
  }

  .load-more-button:hover:not(:disabled) {
    background: linear-gradient(135deg, var(--color-primary-600) 0%, #1e40af 100%);
    box-shadow: var(--shadow-md);
    transform: translateY(-1px);
  }

  .load-more-button:disabled {
    opacity: 0.7;
    cursor: not-allowed;
    transform: none;
  }

  .load-more-spinner {
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .load-more-icon {
    width: 1.25rem;
    height: 1.25rem;
    transition: transform var(--duration-normal) var(--ease-out);
  }

  .load-more-button:hover:not(:disabled) .load-more-icon {
    transform: translateY(2px);
  }

  /* End of Results */
  .end-of-results {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.75rem;
    padding: 2rem;
    border-top: 1px solid rgba(148, 163, 184, 0.2);
    background: linear-gradient(135deg, var(--surface-page) 0%, #f1f5f9 100%);
    text-align: center;
  }

  .end-of-results-icon {
    width: 4rem;
    height: 4rem;
    background: linear-gradient(135deg, #dcfce7 0%, #bbf7d0 100%);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid rgba(34, 197, 94, 0.2);
  }

  .end-of-results-text {
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-gray-700);
    margin: 0;
  }

  .end-of-results-count {
    font-size: 0.875rem;
    color: #64748b;
    margin: 0;
  }

  @media (min-width: 640px) {
    .results-summary {
      flex-direction: row;
    }
  }

  @media (max-width: 768px) {
    .container {
      padding-left: 1rem;
      padding-right: 1rem;
    }

    .filter-group {
      min-width: auto;
    }

    .results-summary {
      flex-direction: column;
      gap: 0.5rem;
      align-items: flex-start;
    }

    .rtl .results-summary {
      align-items: flex-end;
    }

    .content-card {
      padding: 1rem;
      gap: 0.75rem;
    }

    .card-avatar .avatar-fallback,
    .card-avatar .avatar-unknown {
      width: 2.5rem;
      height: 2.5rem;
    }

    .card-header {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.5rem;
    }

    .rtl .card-header {
      align-items: flex-end;
    }

    .card-stats {
      flex-direction: column;
      gap: 0.75rem;
      align-items: stretch;
    }

    .stats-left {
      justify-content: space-between;
    }

    .stats-right {
      justify-content: center;
    }

    .load-more-button {
      padding: 0.75rem 1.5rem;
      font-size: 0.875rem;
    }

    .tags-container {
      gap: 0.375rem;
    }

    .tags-header {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.5rem;
    }

    .rtl .tags-header {
      align-items: flex-end;
    }

    .selected-tags-header {
      flex-direction: column;
      align-items: flex-start;
      gap: 0.5rem;
    }

    .rtl .selected-tags-header {
      align-items: flex-end;
    }

    .header-actions {
      width: 100%;
      justify-content: flex-start;
    }

    .csv-button {
      flex: 1;
      justify-content: center;
    }
  }
</style>
