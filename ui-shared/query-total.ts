// dmart reports a pagination `total` of **-1** when the count was skipped —
// either because the request sent `retrieve_total: false`, or because the
// deployment sets `RETRIEVE_TOTAL_DEFAULT=false` and the request omitted the
// field. -1 is a sentinel meaning "not counted", not a count.
//
// It has to be filtered at every read, because the idioms that look like they
// already handle a missing total do not handle this one: `?? 0` only catches
// null/undefined, and `|| records.length` treats -1 as truthy. Either way -1
// reaches page arithmetic, where `Math.ceil(-1 / limit)` yields -0 and the
// pager renders "1 of 0" with no page buttons.
//
// Returns `fallback` for the sentinel, for a negative or non-finite value, and
// for a missing one — so a caller keeps whatever it already treated as
// "unknown" (usually 0, or the number of records on the current page).
export function resolveTotal(total: unknown, fallback = 0): number {
  const n = toCount(total);
  return n === null ? fallback : n;
}

// True when the server explicitly told us it did not count. Distinct from
// "the total happens to be 0" — a caller that wants to hide a page count
// rather than show a wrong one should branch on this.
export function isTotalUnknown(total: unknown): boolean {
  return toCount(total) === null;
}

// null means "no usable count here". Note the explicit null/undefined/""
// guard: Number(null) and Number("") are both 0, so without it a missing
// total would read as a genuine count of zero — the exact confusion this
// module exists to prevent.
function toCount(total: unknown): number | null {
  if (total === null || total === undefined || total === "") return null;
  const n = typeof total === "number" ? total : Number(total);
  return Number.isFinite(n) && n >= 0 ? n : null;
}
