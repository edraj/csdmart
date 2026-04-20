<script lang="ts">
  import { onDestroy, onMount, tick } from "svelte";
  import {
    getEntities,
    getEntityAttachmentsCount,
  } from "@/lib/dmart_services";
  import { formatDate, renderStateString } from "@/lib/helpers";
  import { goto } from "@roxi/routify";
  import { _, dir } from "@/i18n";
  import SkeletonBlock from "@/components/SkeletonBlock.svelte";

  $goto;
  let isProjectBeingFetched = $state(false);
  let modalOpen = $state(false);
  let searchString = $state("");
  let entities: any[] = $state([]);
  let searchInput: any = $state(null);
  let modalInput: HTMLInputElement | null = $state(null);
  let triggerElement: HTMLDivElement | null = $state(null);
  let previouslyFocused: Element | null = null;
  let isMac = $state(false);

  function toggleModal() {
    if (modalOpen) {
      closeModal();
    } else {
      openModal();
    }
  }

  function openModal() {
    if (modalOpen) return;
    previouslyFocused = document.activeElement;
    modalOpen = true;
    tick().then(() => modalInput?.focus());
    if (searchString.trim()) {
      setTimeout(() => handleSearchChange(), 100);
    }
  }

  function closeModal() {
    if (!modalOpen) return;
    modalOpen = false;
    entities = [];
    const toRestore = previouslyFocused as HTMLElement | null;
    previouslyFocused = null;
    if (toRestore && typeof toRestore.focus === "function") {
      toRestore.focus();
    }
  }

  function handleGlobalKeydown(e: KeyboardEvent) {
    const isShortcut = (e.key === "k" || e.key === "K") && (e.metaKey || e.ctrlKey);
    if (isShortcut) {
      const target = e.target as HTMLElement | null;
      const isEditable =
        target &&
        (target.tagName === "INPUT" ||
          target.tagName === "TEXTAREA" ||
          (target as HTMLElement).isContentEditable);
      if (isEditable && target !== modalInput) return;
      e.preventDefault();
      if (modalOpen) closeModal();
      else openModal();
      return;
    }
    if (e.key === "Escape" && modalOpen) {
      e.preventDefault();
      closeModal();
    }
  }

  function handleModalKeydown(e: KeyboardEvent) {
    if (e.key !== "Tab" || !modalOpen) return;
    const container = (e.currentTarget as HTMLElement) ?? null;
    if (!container) return;
    const focusables = container.querySelectorAll<HTMLElement>(
      'a[href], button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    if (focusables.length === 0) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  onMount(() => {
    isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPod|iPad/i.test(navigator.platform);
    window.addEventListener("keydown", handleGlobalKeydown);
  });

  let timeout: any;
  async function handleSearchChange() {
  if (searchString.trim() && !modalOpen) {
    openModal();
  }

  if (!searchString.trim()) {
    modalOpen = false;
    entities = [];
    return;
  }

  try {
    if (timeout) clearTimeout(timeout);

    timeout = setTimeout(async () => {
      isProjectBeingFetched = true;

      const results: any = await getEntities({
        limit: 15,
        offset: 0,
        shortname: "",
        search: searchString,
      } as any);

      if (results === null) {
        isProjectBeingFetched = false;
        return;
      }

      const _entities: any[] = [];
      for (const item of results as any[]) {
        const counts = await getEntityAttachmentsCount(
          item.shortname,
          item.space_name,
          item.subpath,
        );

        _entities.push({
          shortname: item.shortname,
          owner: item.attributes.owner_shortname,
          tags: item.attributes.tags,
          state: item.attributes.state,
          is_active: item.attributes.is_active,
          updated_at: formatDate(item.attributes.updated_at),
          ...item.attributes.payload.body,
          ...counts[0].attributes,
        });
      }

      entities = _entities;
      isProjectBeingFetched = false;
    }, 500);
  } catch (e) {
    isProjectBeingFetched = false;
  }
}

  function gotoEntityDetails(entity: any) {
    $goto("/dashboard/[shortname]", {
      shortname: entity.shortname,
    });

    modalOpen = false;
  }

  onDestroy(() => {
    if (timeout) {
      clearTimeout(timeout);
    }
    if (typeof window !== "undefined") {
      window.removeEventListener("keydown", handleGlobalKeydown);
    }
  });
</script>

<div
  class="search-trigger"
  bind:this={triggerElement}
  aria-label={$_("route_labels.aria_search")}
  title={$_("route_labels.aria_search")}
>
  <svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M7.33333 12.6667C10.2789 12.6667 12.6667 10.2789 12.6667 7.33333C12.6667 4.38781 10.2789 2 7.33333 2C4.38781 2 2 4.38781 2 7.33333C2 10.2789 4.38781 12.6667 7.33333 12.6667Z" stroke="currentColor" stroke-width="1.33333" stroke-linecap="round" stroke-linejoin="round"/>
    <path d="M14 14L11.1333 11.1333" stroke="currentColor" stroke-width="1.33333" stroke-linecap="round" stroke-linejoin="round"/>
  </svg>

  <input
    bind:this={searchInput}
    type="text"
    placeholder={$_("route_labels.search_placeholder_short")}
    bind:value={searchString}
    onkeyup={handleSearchChange}
    class="search-trigger-input"
  />

  <kbd class="search-trigger-kbd" aria-hidden="true">
    {isMac ? "⌘" : "Ctrl"}<span class="kbd-sep">+</span>K
  </kbd>
</div>
{#if modalOpen}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="backdrop-overlay" onclick={closeModal} onkeydown={() => {}}></div>
  <div
    class="search-modal-wrapper"
    role="dialog"
    aria-modal="true"
    aria-labelledby="search-modal-heading"
    tabindex="-1"
    onkeydown={handleModalKeydown}
  >
    <div class="search-modal">
      <h2 id="search-modal-heading" class="sr-only">
        {$_("SearchEntities")}
      </h2>
      <div class="search-modal-header">
        <div class="search-modal-input-wrap">
          <svg
            class="search-modal-icon"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
            aria-hidden="true"
          >
            <path
              stroke-linecap="round"
              stroke-linejoin="round"
              stroke-width="2"
              d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
            ></path>
          </svg>
          <label for="search-modal-input" class="sr-only">
            {$_("SearchEntities")}
          </label>
          <input
            id="search-modal-input"
            type="text"
            placeholder={$_("SearchEntities")}
            bind:value={searchString}
            bind:this={modalInput}
            onkeyup={handleSearchChange}
            class="search-modal-input"
          />
        </div>
        <button
          onclick={closeModal}
          class="search-modal-close"
          aria-label={$_("route_labels.aria_close")}
        >
          <svg
            class="w-5 h-5"
            fill="none"
            stroke="currentColor"
            viewBox="0 0 24 24"
            aria-hidden="true"
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

      <div
        class="search-modal-body"
        role="region"
        aria-label="Search Results"
        aria-live="polite"
        aria-busy={isProjectBeingFetched}
      >
        {#if searchString.length === 0}
          <div class="search-empty-state">
            <svg
              class="search-empty-icon"
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
            <p class="search-empty-title">{$_("Searchplaceholder")}</p>
            <p class="search-empty-hint">{$_("Searchplaceholder2")}</p>
          </div>
        {:else}
          {#if isProjectBeingFetched}
            <div class="search-skeleton-list" aria-hidden="true">
              {#each Array(4) as _skeletonRow}
                <div class="search-skeleton-row">
                  <div class="search-skeleton-col">
                    <SkeletonBlock width="55%" height="1.0625rem" />
                    <SkeletonBlock width="30%" height="0.75rem" />
                  </div>
                  <div class="search-skeleton-col search-skeleton-col-meta">
                    <SkeletonBlock width="3rem" height="0.75rem" />
                    <SkeletonBlock width="4rem" height="1.125rem" radius="var(--radius-full)" />
                  </div>
                </div>
              {/each}
            </div>
          {/if}

          {#if entities.length === 0 && !isProjectBeingFetched}
            <div class="search-empty-state">
              <svg
                class="search-empty-icon"
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
              <p class="search-empty-title">{$_("NoResults")}</p>
              <p class="search-empty-hint">{$_("NoResults2")}</p>
            </div>
          {:else}
            <div class="search-results-list">
              {#each entities as entity}
                <div
                  class="search-result-item"
                  role="button"
                  tabindex="0"
                  onkeydown={() => gotoEntityDetails(entity)}
                  onclick={() => gotoEntityDetails(entity)}
                >
                  <div class="search-result-content">
                    <div class="search-result-info">
                      <h3 class="search-result-title">
                        {entity.title}
                      </h3>
                      <p class="search-result-date">
                        {entity.updated_at}
                      </p>
                    </div>

                    <div class="search-result-meta">
                      <div class="search-result-stats">
                        <span class="stat-reactions">
                          <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                            <path d="M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z" />
                          </svg>
                          {entity.reaction ?? 0}
                        </span>
                        <span class="stat-comments">
                          <svg class="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                            <path d="M20 2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h4l4 4 4-4h4c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2z" />
                          </svg>
                          {entity.comment ?? 0}
                        </span>
                      </div>

                      <span class="search-result-badge">
                        {renderStateString(entity)}
                      </span>
                    </div>
                  </div>
                </div>
              {/each}
            </div>
          {/if}
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  .search-trigger {
    margin-inline-start: 0.5rem;
    margin-inline-end: 0.5rem;
    width: 100%;
    max-width: 400px;
    display: flex;
    align-items: center;
    justify-content: flex-start;
    height: 2.375rem;
    border-radius: var(--radius-xl);
    background: var(--color-gray-50);
    padding: 0.5rem 0.625rem;
    color: var(--color-gray-400);
    border: 1px solid transparent;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .search-trigger:focus-within {
    border-color: var(--color-primary-200);
    background: white;
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.08);
  }

  .search-trigger-input {
    width: 100%;
    background: transparent;
    border: none;
    outline: none;
    color: var(--color-gray-900);
    font-size: 0.875rem;
    padding-inline-start: 0.5rem;
  }

  .search-trigger-input::placeholder {
    color: var(--color-gray-400);
  }

  .search-trigger-kbd {
    display: inline-flex;
    align-items: center;
    gap: 0.125rem;
    padding: 0.125rem 0.375rem;
    margin-inline-start: 0.5rem;
    border: 1px solid var(--color-gray-200);
    border-radius: var(--radius-sm);
    background: var(--surface-card);
    color: var(--color-gray-500);
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

  /* ── Modal ── */
  .search-modal-wrapper {
    position: fixed;
    inset: 0;
    z-index: 50;
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding-top: 4rem;
    padding-inline: 1rem;
  }

  .search-modal {
    position: relative;
    width: 100%;
    max-width: 56rem;
    background: var(--surface-card);
    border-radius: var(--radius-xl);
    box-shadow: var(--shadow-xl);
    border: 1px solid var(--color-gray-200);
    max-height: 80vh;
    display: flex;
    flex-direction: column;
    animation: scaleIn var(--duration-normal) var(--ease-out);
  }

  .search-modal-header {
    padding: 1.5rem;
    border-bottom: 1px solid var(--color-gray-100);
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .search-modal-input-wrap {
    flex: 1;
    position: relative;
  }

  .search-modal-icon {
    position: absolute;
    left: 0.75rem;
    top: 50%;
    transform: translateY(-50%);
    width: 1.25rem;
    height: 1.25rem;
    color: var(--color-gray-400);
  }

  .search-modal-input {
    width: 100%;
    padding: 0.75rem 1rem 0.75rem 2.5rem;
    background: var(--color-gray-50);
    border: 1.5px solid var(--color-gray-200);
    border-radius: var(--radius-lg);
    font-size: 0.9375rem;
    color: var(--color-gray-900);
    transition: all var(--duration-normal) var(--ease-out);
  }

  .search-modal-input::placeholder { color: var(--color-gray-400); }

  .search-modal-input:focus {
    outline: none;
    border-color: var(--color-primary-400);
    background: white;
    box-shadow: 0 0 0 3px rgba(99, 102, 241, 0.1);
  }

  .search-modal-close {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 2.5rem;
    height: 2.5rem;
    border-radius: var(--radius-lg);
    background: var(--color-gray-100);
    border: none;
    color: var(--color-gray-600);
    cursor: pointer;
    transition: all var(--duration-fast) ease;
    flex-shrink: 0;
  }

  .search-modal-close:hover {
    background: var(--color-gray-200);
    color: var(--color-gray-800);
  }

  .search-modal-body {
    flex: 1;
    overflow-y: auto;
    padding: 1.5rem;
  }

  /* ── Empty / Loading States ── */
  .search-empty-state {
    text-align: center;
    padding: 3rem 0;
  }

  .search-empty-icon {
    width: 3rem;
    height: 3rem;
    color: var(--color-gray-300);
    margin: 0 auto 1rem;
  }

  .search-empty-title {
    color: var(--color-gray-500);
    font-size: 1.125rem;
  }

  .search-empty-hint {
    color: var(--color-gray-400);
    font-size: 0.875rem;
    margin-top: 0.25rem;
  }

  .search-skeleton-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .search-skeleton-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 1rem;
    border: 1px solid var(--color-gray-100);
    border-radius: var(--radius-lg);
  }

  .search-skeleton-col {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    flex: 1;
    min-width: 0;
  }

  .search-skeleton-col-meta {
    align-items: flex-end;
    flex: 0 0 auto;
  }

  /* ── Results List ── */
  .search-results-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .search-result-item {
    padding: 1rem;
    border-radius: var(--radius-lg);
    border: 1px solid var(--color-gray-200);
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .search-result-item:hover {
    border-color: var(--color-primary-200);
    background: var(--color-primary-50);
  }

  .search-result-content {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  @media (min-width: 640px) {
    .search-result-content {
      flex-direction: row;
      align-items: center;
      justify-content: space-between;
    }
  }

  .search-result-info {
    flex: 1;
    min-width: 0;
  }

  .search-result-title {
    font-weight: 600;
    color: var(--color-gray-900);
    font-size: 1.0625rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    transition: color var(--duration-fast) ease;
  }

  .search-result-item:hover .search-result-title {
    color: var(--color-primary-700);
  }

  .search-result-date {
    font-size: 0.875rem;
    color: var(--color-gray-500);
    margin-top: 0.25rem;
  }

  .search-result-meta {
    display: flex;
    align-items: center;
    gap: 1.5rem;
    font-size: 0.875rem;
  }

  .search-result-stats {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  .stat-reactions {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    color: var(--color-error);
  }

  .stat-comments {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    color: var(--color-info);
  }

  .search-result-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.125rem 0.625rem;
    border-radius: var(--radius-full);
    font-size: 0.75rem;
    font-weight: 500;
    background: var(--color-gray-100);
    color: var(--color-gray-700);
  }
</style>
