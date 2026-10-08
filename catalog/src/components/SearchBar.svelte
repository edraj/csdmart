<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { streamEntitiesAcrossSpaces } from "@/lib/dmart_services";
  import { params } from "@roxi/routify";
  import { _, locale } from "@/i18n";
  import { formatDate } from "@/lib/format";
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";
  import { encodeSubpath, ROOT_SUBPATH_SEGMENT, withBase } from "@/lib/paths";

  // One hit in the dropdown, flattened from the query record.
  interface SearchEntity {
    shortname: string;
    space_name: string;
    folder: string;
    subpath: string;
    resource_type: string;
    created_at: string;
  }

  let isProjectBeingFetched = $state(false);
  let searchString = $state("");
  let entities: SearchEntity[] = $state([]);
  let searchInput: HTMLInputElement | null = $state(null);
  let triggerElement: HTMLDivElement | null = $state(null);
  let dropdownElement: HTMLDivElement | null = $state(null);

  type TagItem = { type: "space" | "folder"; value: string; label: string };
  let tags: TagItem[] = $state([]);
  let showDropdown = $state(false);

  const routeParams = $derived($params as Record<string, string | undefined>);
  const currentSpace = $derived(routeParams.space_name || "");
  const currentFolder = $derived(routeParams.subpath || "");

  let availableTagOptions: TagItem[] = $derived.by(() => {
    const opts: TagItem[] = [];
    if (currentSpace) {
      opts.push({
        type: "space",
        value: currentSpace,
        label: $_("route_labels.search_current_space", { values: { value: currentSpace } }),
      });
    }
    if (currentFolder) {
      opts.push({
        type: "folder",
        value: currentFolder,
        label: $_("route_labels.search_current_folder", { values: { value: currentFolder } }),
      });
    }
    return opts.filter((o) => !tags.find((t) => t.type === o.type));
  });

  function openDropdown() {
    showDropdown = true;
  }

  function addTag(tag: TagItem) {
    if (tags.find((t) => t.type === tag.type)) return;
    tags = [...tags, tag];
    searchInput?.focus();
    if (searchString.trim()) handleSearchChange();
  }

  function removeTag(tag: TagItem) {
    tags = tags.filter((t) => t.type !== tag.type);
    if (searchString.trim()) handleSearchChange();
  }

  function handleDocumentClick(e: MouseEvent) {
    const t = e.target as Node;
    if (
      triggerElement &&
      !triggerElement.contains(t) &&
      dropdownElement &&
      !dropdownElement.contains(t)
    ) {
      showDropdown = false;
    }
  }

  let isMac = $state(false);

  function handleKeydown(e: KeyboardEvent) {
    // Cmd/Ctrl+K — focus the search input from anywhere in the app. Skip
    // when the user is already typing in another input/textarea/contentEditable
    // so the shortcut doesn't fight a form being filled out elsewhere.
    if ((e.key === "k" || e.key === "K") && (e.metaKey || e.ctrlKey)) {
      const target = e.target as HTMLElement | null;
      const inEditable =
        target &&
        (target.tagName === "INPUT" ||
          target.tagName === "TEXTAREA" ||
          target.isContentEditable);
      if (inEditable && target !== searchInput) return;
      e.preventDefault();
      if (showDropdown && document.activeElement === searchInput) {
        showDropdown = false;
        searchInput?.blur();
      } else {
        showDropdown = true;
        searchInput?.focus();
      }
      return;
    }
    if (e.key === "Escape" && showDropdown) {
      showDropdown = false;
      searchInput?.blur();
    }
  }

  let timeout: ReturnType<typeof setTimeout> | undefined;
  let searchToken = 0;
  let activeStream: { cancel: () => void } | null = null;

  function cancelActiveStream() {
    if (activeStream) {
      activeStream.cancel();
      activeStream = null;
    }
  }

  function handleSearchChange() {
    if (searchString.trim()) showDropdown = true;

    if (!searchString.trim()) {
      entities = [];
      cancelActiveStream();
      isProjectBeingFetched = false;
      return;
    }

    if (timeout) clearTimeout(timeout);

    timeout = setTimeout(() => {
      const token = ++searchToken;
      cancelActiveStream();
      entities = [];
      isProjectBeingFetched = true;

      const spaceTag = tags.find((t) => t.type === "space");
      const folderTag = tags.find((t) => t.type === "folder");

      const stream = streamEntitiesAcrossSpaces(
        searchString,
        (records, space) => {
          if (token !== searchToken) return;

          const enriched: SearchEntity[] = records.map((item) => {
            const subpath: string = item.subpath ?? "";
            const folder = subpath.replace(/^\/+|\/+$/g, "") || "/";
            return {
              shortname: item.shortname,
              space_name: item.space_name ?? space,
              folder,
              subpath,
              resource_type: item.resource_type ?? "content",
              created_at: formatDate(item.attributes?.created_at, "datetime", $locale),
            };
          });

          entities = [...entities, ...enriched];
        },
        {
          spaceFilter: spaceTag?.value,
          subpathFilter: folderTag?.value,
        },
      );

      activeStream = stream;

      stream.done.finally(() => {
        if (token === searchToken) {
          isProjectBeingFetched = false;
          activeStream = null;
        }
      });
    }, 500);
  }

  // The admin route for a hit: a folder opens its listing, an entry its page,
  // and an entry at the root (no folder segment to put in the URL) opens the
  // space. Hits are real links so middle-click and "open in new tab" work.
  function entityPath(entity: SearchEntity): string {
    const parentRouteSubpath = encodeSubpath(entity.subpath);
    const atRoot = parentRouteSubpath === ROOT_SUBPATH_SEGMENT;
    const space = encodeURIComponent(entity.space_name);
    if (entity.resource_type === "folder") {
      const folderSubpath = atRoot ? entity.shortname : `${parentRouteSubpath}-${entity.shortname}`;
      return `/dashboard/admin/${space}/${encodeURIComponent(folderSubpath)}`;
    }
    if (!atRoot) {
      return (
        `/dashboard/admin/${space}/${encodeURIComponent(parentRouteSubpath)}` +
        `/${encodeURIComponent(entity.shortname)}/${encodeURIComponent(entity.resource_type)}`
      );
    }
    return `/dashboard/admin/${space}`;
  }

  onMount(() => {
    isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPod|iPad/i.test(navigator.platform);
    document.addEventListener("click", handleDocumentClick);
    document.addEventListener("keydown", handleKeydown);
  });

  onDestroy(() => {
    if (timeout) clearTimeout(timeout);
    cancelActiveStream();
    if (typeof document !== "undefined") {
      document.removeEventListener("click", handleDocumentClick);
      document.removeEventListener("keydown", handleKeydown);
    }
  });
</script>

<div class="search-trigger-wrap">
  <div class="search-trigger" bind:this={triggerElement}>
    <svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
      <path d="M7.33333 12.6667C10.2789 12.6667 12.6667 10.2789 12.6667 7.33333C12.6667 4.38781 10.2789 2 7.33333 2C4.38781 2 2 4.38781 2 7.33333C2 10.2789 4.38781 12.6667 7.33333 12.6667Z" stroke="currentColor" stroke-width="1.33333" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="M14 14L11.1333 11.1333" stroke="currentColor" stroke-width="1.33333" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>

    {#each tags as tag (tag.type)}
      <span class="search-tag-chip">
        <span class="search-tag-chip-label">{tag.label}</span>
        <button
          type="button"
          class="search-tag-chip-remove"
          aria-label={$_("route_labels.search_remove_tag")}
          onclick={(e) => {
            e.stopPropagation();
            removeTag(tag);
          }}
        >
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
            <path d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </span>
    {/each}

    <input
      bind:this={searchInput}
      type="search"
      placeholder={$_("route_labels.search_placeholder_short")}
      aria-label={$_("route_labels.aria_search")}
      bind:value={searchString}
      oninput={handleSearchChange}
      onfocus={openDropdown}
      onclick={openDropdown}
      class="search-trigger-input"
    />

    <kbd class="search-trigger-kbd" aria-hidden="true">
      {isMac ? "⌘" : "Ctrl"}<span class="kbd-sep">+</span>K
    </kbd>
  </div>

  {#if showDropdown}
    <div class="search-dropdown" bind:this={dropdownElement} aria-busy={isProjectBeingFetched}>
      {#if searchString.trim().length === 0}
        {#if availableTagOptions.length === 0}
          <div class="search-dropdown-empty">{$_("route_labels.search_no_filters_available")}</div>
        {:else}
          {#each availableTagOptions as opt (opt.type)}
            <button
              type="button"
              class="search-tag-option"
              onclick={() => addTag(opt)}
            >
              <svg class="search-tag-option-icon" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z" />
                <line x1="7" y1="7" x2="7.01" y2="7" />
              </svg>
              <span>{opt.label}</span>
            </button>
          {/each}
        {/if}
      {:else}
        {#if isProjectBeingFetched}
          <div class="search-skeleton-list" aria-hidden="true">
            {#each Array(3) as _row, i (i)}
              <div class="search-skeleton-row">
                <div class="search-skeleton-col">
                  <SkeletonBlock width="55%" height="1rem" />
                  <SkeletonBlock width="30%" height="0.75rem" />
                </div>
                <div class="search-skeleton-col search-skeleton-col-meta">
                  <SkeletonBlock width="4rem" height="0.75rem" />
                </div>
              </div>
            {/each}
          </div>
        {/if}

        {#if entities.length === 0 && !isProjectBeingFetched}
          <div class="search-dropdown-empty">{$_("NoResults")}</div>
        {:else if entities.length > 0}
          <div class="search-results-list">
            {#each entities as entity (`${entity.space_name}/${entity.subpath}/${entity.shortname}`)}
              <a class="search-result-item" href={withBase(entityPath(entity))} onclick={() => (showDropdown = false)}>
                <div class="search-result-content">
                  <div class="search-result-info">
                    <h3 class="search-result-title">{entity.shortname}</h3>
                    <div class="search-result-meta-row">
                      <span class="search-result-meta-pill">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                          <path d="M3 12l2-2 4 4 8-8 4 4" />
                        </svg>
                        {entity.space_name}
                      </span>
                      <span class="search-result-meta-pill">
                        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                          <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" />
                        </svg>
                        {entity.folder}
                      </span>
                    </div>
                  </div>
                  <div class="search-result-date">{entity.created_at}</div>
                </div>
              </a>
            {/each}
          </div>
        {/if}
      {/if}
    </div>
  {/if}
</div>

<style>
  .search-trigger-wrap {
    position: relative;
    margin-inline-start: 0.5rem;
    margin-inline-end: 0.5rem;
    width: 100%;
  }

  .search-trigger {
    width: 100%;
    display: flex;
    align-items: center;
    justify-content: flex-start;
    flex-wrap: wrap;
    gap: 0.25rem;
    min-height: 2.375rem;
    border-radius: var(--radius-card);
    background: var(--color-surface-3);
    padding: 0.25rem 0.625rem;
    color: var(--color-text-faint);
    border: 1px solid transparent;
    transition: all var(--duration-normal) var(--ease-out);
    cursor: text;
  }

  .search-trigger:focus-within {
    border-color: var(--color-primary);
    background: var(--color-surface-2);
    box-shadow: 0 0 0 1px var(--color-primary);
  }

  .search-trigger-input {
    flex: 1 1 6rem;
    min-width: 4rem;
    width: auto;
    background: transparent;
    border: none;
    outline: none;
    color: var(--color-text);
    font-size: 0.875rem;
    padding-inline-start: 0.5rem;
  }

  .search-trigger-input::placeholder {
    color: var(--color-text-faint);
  }

  .search-tag-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.125rem 0.25rem 0.125rem 0.5rem;
    background: var(--color-primary-soft);
    color: var(--color-primary);
    border: 1px solid var(--color-primary);
    border-radius: var(--radius-full);
    font-size: 0.75rem;
    font-weight: 500;
    line-height: 1.25;
    white-space: nowrap;
    max-width: 14rem;
  }

  .search-tag-chip-label {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .search-tag-chip-remove {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 1rem;
    height: 1rem;
    border-radius: var(--radius-full);
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    flex-shrink: 0;
  }

  .search-tag-chip-remove:hover {
    background: var(--color-primary-soft);
    color: var(--color-primary-hover);
  }

  /* ── Unified Dropdown ── */
  .search-dropdown {
    position: absolute;
    top: calc(100% + 0.25rem);
    inset-inline-start: 0;
    inset-inline-end: 0;
    z-index: 60;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-modal);
    padding: 0.375rem;
    display: flex;
    flex-direction: column;
    gap: 0.125rem;
    max-height: 70vh;
    overflow-y: auto;
  }

  .search-dropdown-empty {
    padding: 0.625rem 0.75rem;
    font-size: 0.8125rem;
    color: var(--color-text-faint);
    text-align: center;
  }

  .search-tag-option {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.625rem;
    background: transparent;
    border: none;
    border-radius: var(--radius-control);
    color: var(--color-text-muted);
    font-size: 0.8125rem;
    text-align: start;
    cursor: pointer;
    transition: background var(--duration-fast) ease;
  }

  .search-tag-option:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .search-tag-option-icon {
    color: var(--color-text-faint);
    flex-shrink: 0;
  }

  /* ── Skeleton ── */
  .search-skeleton-list {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .search-skeleton-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 0.5rem 0.625rem;
    border-radius: var(--radius-control);
  }

  .search-skeleton-col {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
    flex: 1;
    min-width: 0;
  }

  .search-skeleton-col-meta {
    align-items: flex-end;
    flex: 0 0 auto;
  }

  /* ── Results ── */
  .search-results-list {
    display: flex;
    flex-direction: column;
    gap: 0.125rem;
  }

  .search-result-item {
    display: block;
    color: inherit;
    text-decoration: none;
    padding: 0.5rem 0.625rem;
    border-radius: var(--radius-control);
    border: 1px solid transparent;
    cursor: pointer;
    transition: background var(--duration-fast) ease, border-color var(--duration-fast) ease;
  }

  .search-result-item:hover,
  .search-result-item:focus-visible {
    background: var(--color-surface-3);
    border-color: var(--color-border);
    outline: none;
  }

  .search-result-content {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
  }

  .search-result-info {
    flex: 1;
    min-width: 0;
  }

  .search-result-title {
    font-weight: 600;
    color: var(--color-text);
    font-size: 0.9375rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .search-result-meta-row {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.375rem;
    margin-top: 0.25rem;
  }

  .search-result-meta-pill {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.0625rem 0.5rem;
    border-radius: var(--radius-full);
    background: var(--color-surface-3);
    color: var(--color-text-muted);
    font-size: 0.6875rem;
    font-weight: 500;
    line-height: 1.4;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .search-result-meta-pill svg {
    flex-shrink: 0;
    color: var(--color-text-faint);
  }

  .search-result-date {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    flex-shrink: 0;
    white-space: nowrap;
  }

  .search-trigger-kbd {
    display: inline-flex;
    align-items: center;
    gap: 0.125rem;
    padding: 0.125rem 0.375rem;
    margin-inline-start: 0.25rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface-2);
    color: var(--color-text-muted);
    font-size: 0.6875rem;
    font-family: var(--font-sans);
    font-weight: var(--font-weight-medium);
    line-height: 1;
    white-space: nowrap;
    flex-shrink: 0;
  }

  .kbd-sep {
    opacity: 0.6;
  }

  @media (max-width: 640px) {
    .search-trigger-kbd { display: none; }
  }
</style>
