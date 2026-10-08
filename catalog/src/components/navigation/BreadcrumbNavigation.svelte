<script lang="ts">
  import { ArrowLeftOutline, ChevronRightOutline, LinkOutline } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { withBase, type Breadcrumb } from "@/lib/paths";
  import { copyToClipboard } from "@/lib/toast";

  // Header of an entry page: a back button, the breadcrumb trail built by
  // catalogBreadcrumbs() (lib/paths.ts) and a copy-link button that reports
  // through a toast. The last crumb (path: null) is the current page.
  let {
    breadcrumbs = [],
    onGoBack,
  }: { breadcrumbs?: Breadcrumb[]; onGoBack?: () => void } = $props();

  const parentCrumb = $derived(
    breadcrumbs.length > 1 ? breadcrumbs[breadcrumbs.length - 2] : null,
  );

  function goBack() {
    if (onGoBack) onGoBack();
    else history.back();
  }

  async function copyLink() {
    await copyToClipboard(window.location.href, {
      copied: $_("ui.link_copied"),
      failed: $_("ui.copy_failed"),
    });
  }
</script>

<header class="page-header">
  <div class="header-content">
    <button type="button" onclick={goBack} class="back-button">
      <ArrowLeftOutline size="sm" class="rtl:rotate-180 shrink-0" aria-hidden="true" />
      <span>
        {$_("common.back_to")}
        {parentCrumb ? parentCrumb.name : $_("common.list")}
      </span>
    </button>

    <button type="button" onclick={copyLink} class="copy-link-btn">
      <LinkOutline size="sm" aria-hidden="true" />
      <span>{$_("common.copy_link")}</span>
    </button>
  </div>

  {#if breadcrumbs.length > 0}
    <nav aria-label={$_("ui.breadcrumb")} class="trail">
      <ol>
        {#each breadcrumbs as crumb, i (`${i}:${crumb.path ?? crumb.name}`)}
          <li>
            {#if i > 0}
              <ChevronRightOutline size="xs" class="rtl:rotate-180 sep" aria-hidden="true" />
            {/if}
            {#if crumb.path}
              <a href={withBase(crumb.path)}>{crumb.name}</a>
            {:else}
              <span aria-current="page">{crumb.name}</span>
            {/if}
          </li>
        {/each}
      </ol>
    </nav>
  {/if}
</header>

<style>
  .page-header {
    padding: 1rem 0 0.5rem;
    max-width: 80rem;
    margin: 0 auto 1rem;
  }

  .header-content {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 0.5rem 1rem;
    padding: 0 var(--space-page-x);
  }

  .back-button {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    background: none;
    border: none;
    color: var(--color-text-muted);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 500;
    padding: 0.25rem 0;
    transition: color var(--duration-fast) ease;
  }

  .back-button:hover {
    color: var(--color-text);
  }

  .copy-link-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    background-color: var(--color-surface-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-full);
    padding: 0.375rem 1rem;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-text-muted);
    cursor: pointer;
    transition: background-color var(--duration-fast) ease, color var(--duration-fast) ease;
  }

  .copy-link-btn:hover {
    background-color: var(--color-surface-2);
    color: var(--color-text);
  }

  .trail {
    padding: 0.75rem var(--space-page-x) 0;
    font-size: 0.8125rem;
  }

  .trail ol {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.25rem 0.375rem;
  }

  .trail li {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    min-width: 0;
  }

  .trail :global(.sep) {
    color: var(--color-text-faint);
    flex-shrink: 0;
  }

  .trail a {
    color: var(--color-text-muted);
    text-decoration: none;
    border-radius: var(--radius-control);
  }

  .trail a:hover {
    color: var(--color-primary);
    text-decoration: underline;
  }

  .trail [aria-current="page"] {
    color: var(--color-text);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 24rem;
  }
</style>
