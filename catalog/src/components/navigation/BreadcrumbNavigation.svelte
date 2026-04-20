<script lang="ts">
  import { _ } from "@/i18n";
  import type { Breadcrumb } from "@/lib/utils/postUtils";

  export let breadcrumbs: Breadcrumb[];
  export let onGoBack: () => void;

  function copyLink() {
    navigator.clipboard.writeText(window.location.href);
  }

  $: parentCrumb =
    breadcrumbs && breadcrumbs.length > 1
      ? breadcrumbs[breadcrumbs.length - 2]
      : null;
</script>

<header class="page-header">
  <div class="header-content">
    <button aria-label="Go back" onclick={onGoBack} class="back-button">
      <svg
        class="back-icon"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
      >
        <path
          stroke-linecap="round"
          stroke-linejoin="round"
          stroke-width="2"
          d="M10 19l-7-7m0 0l7-7m-7 7h18"
        />
      </svg>
      <span
        >{$_("common.back_to", { default: "Back to" })}
        {parentCrumb
          ? parentCrumb.name
          : $_("common.list", { default: "List" })}</span
      >
    </button>

    <button aria-label="Copy link" onclick={copyLink} class="copy-link-btn">
      <svg
        class="link-icon"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
      >
        <path
          stroke-linecap="round"
          stroke-linejoin="round"
          stroke-width="2"
          d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1"
        />
      </svg>
      <span>{$_("common.copy_link", { default: "Copy link" })}</span>
    </button>
  </div>
</header>

<style>
  .page-header {
    background: transparent;
    padding: 1rem 0;
    margin-bottom: 1.5rem;
    max-width: 80rem;
    margin: 0 auto;
  }

  .header-content {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 1.5rem;
  }

  .back-button {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: none;
    border: none;
    color: var(--color-gray-500);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 500;
    padding: 0;
    transition: color var(--duration-fast) ease;
  }

  .back-button:hover {
    color: var(--color-gray-900);
  }

  .back-icon {
    width: 1rem;
    height: 1rem;
  }

  .copy-link-btn {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    background-color: var(--color-gray-100);
    border: none;
    border-radius: var(--radius-full);
    padding: 0.375rem 1rem;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-gray-600);
    cursor: pointer;
    transition: background-color var(--duration-fast) ease;
  }

  .copy-link-btn:hover {
    background-color: var(--color-gray-200);
  }

  .link-icon {
    width: 0.875rem;
    height: 0.875rem;
  }
</style>
