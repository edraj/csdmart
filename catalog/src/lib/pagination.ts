// The page numbers a pager shows: first and last, a window around the
// current page, and an ellipsis where pages were skipped. Pure, so the table
// never rebuilds an Array(totalPages) on every render (review perf #20).
//
//   pageWindow(1, 3)   -> [1, 2, 3]
//   pageWindow(5, 20)  -> [1, "…", 4, 5, 6, "…", 20]
//   pageWindow(20, 20) -> [1, "…", 19, 20]

export const ELLIPSIS = "…";

export type PageItem = number | typeof ELLIPSIS;

export function pageWindow(current: number, total: number, siblings = 1): PageItem[] {
  if (!Number.isFinite(total) || total < 1) return [];
  const page = Math.min(Math.max(1, Math.trunc(current)), total);
  const span = 2 * siblings + 5; // first, last, window, two ellipses
  if (total <= span) {
    return Array.from({ length: total }, (_, i) => i + 1);
  }

  const start = Math.max(2, page - siblings);
  const end = Math.min(total - 1, page + siblings);
  const items: PageItem[] = [1];
  if (start > 2) items.push(ELLIPSIS);
  for (let p = start; p <= end; p++) items.push(p);
  if (end < total - 1) items.push(ELLIPSIS);
  items.push(total);
  return items;
}

/** 1-based "showing X–Y of N" bounds for a page; zero when there is nothing. */
export function pageRange(page: number, perPage: number, total: number): { start: number; end: number } {
  if (total <= 0 || perPage <= 0) return { start: 0, end: 0 };
  const start = (Math.max(1, page) - 1) * perPage + 1;
  return { start: Math.min(start, total), end: Math.min(start + perPage - 1, total) };
}
