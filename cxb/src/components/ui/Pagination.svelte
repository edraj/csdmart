<script lang="ts">
    import { ChevronLeftOutline, ChevronRightOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import { formatNumber } from "@/utils/format";
    import { pageCount, pageRange, visiblePages } from "@/utils/paging";

    let {
        page,
        pageSize,
        total,
        onPageChange,
        onPageSizeChange,
        pageSizes = [15, 30, 50, 100],
        maxPageDisplay = 5,
        showSummary = true,
        class: className = "",
    }: {
        /** 1-based current page. */
        page: number;
        pageSize: number;
        /** Total number of rows; null while unknown. */
        total: number | null | undefined;
        onPageChange: (page: number) => void;
        /** Omit to hide the rows-per-page select. */
        onPageSizeChange?: (pageSize: number) => void;
        pageSizes?: number[];
        maxPageDisplay?: number;
        showSummary?: boolean;
        class?: string;
    } = $props();

    const totalPages = $derived(pageCount(total, pageSize));
    const current = $derived(Math.min(Math.max(1, page), totalPages));
    const pages = $derived(visiblePages(current, totalPages, maxPageDisplay));
    const range = $derived(pageRange(current, pageSize, total ?? 0));

    function go(target: number) {
        if (target < 1 || target > totalPages || target === current) return;
        onPageChange(target);
    }

    const buttonBase =
        "inline-flex items-center justify-center min-w-9 h-9 px-2 text-sm border border-border bg-surface-2 text-text hover:bg-surface-3 focus-visible:outline-2 focus-visible:outline-offset-[-2px] focus-visible:outline-primary disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:bg-surface-2 cursor-pointer transition-colors";
</script>

<nav
    class="flex flex-col sm:flex-row flex-wrap items-center justify-between gap-3 {className}"
    aria-label={$_("pagination")}
>
    {#if onPageSizeChange}
        <label class="flex items-center gap-2 text-sm text-text-muted">
            <span>{$_("items_per_page")}</span>
            <select
                class="rounded-control border border-border bg-surface-2 text-text text-sm py-1.5 ps-2 pe-8 tabular-nums"
                value={String(pageSize)}
                onchange={(e) => onPageSizeChange(Number(e.currentTarget.value))}
            >
                {#each pageSizes as size (size)}
                    <option value={String(size)}>{size}</option>
                {/each}
            </select>
        </label>
    {/if}

    {#if showSummary}
        <p class="text-sm text-text-muted tabular-nums">
            {$_("showing_entries", {
                values: {
                    from: formatNumber(range.from),
                    to: formatNumber(range.to),
                    total: formatNumber(total ?? 0),
                },
            })}
        </p>
    {/if}

    <ul class="inline-flex items-center -space-x-px rtl:space-x-reverse">
        <li>
            <button
                type="button"
                class="{buttonBase} rounded-s-control"
                aria-label={$_("previous")}
                disabled={current <= 1}
                onclick={() => go(current - 1)}
            >
                <ChevronLeftOutline class="w-4 h-4 rtl:rotate-180" aria-hidden="true" />
            </button>
        </li>
        {#each pages as p (p)}
            <li>
                <button
                    type="button"
                    class="{buttonBase} tabular-nums {p === current
                        ? 'bg-primary! text-text-on-primary! border-primary! hover:bg-primary-hover!'
                        : ''}"
                    aria-current={p === current ? "page" : undefined}
                    aria-label={$_("page_n", { values: { page: formatNumber(p) } })}
                    onclick={() => go(p)}
                >
                    {formatNumber(p)}
                </button>
            </li>
        {/each}
        <li>
            <button
                type="button"
                class="{buttonBase} rounded-e-control"
                aria-label={$_("next")}
                disabled={current >= totalPages}
                onclick={() => go(current + 1)}
            >
                <ChevronRightOutline class="w-4 h-4 rtl:rotate-180" aria-hidden="true" />
            </button>
        </li>
    </ul>
</nav>
