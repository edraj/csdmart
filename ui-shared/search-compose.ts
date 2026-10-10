/**
 * Joins search fragments so that every fragment holds for every result:
 * `(a) (b)`.
 *
 * The grammar's AND binds tighter than `or`, so appending a fixed filter to
 * what someone typed (`x or y` + ` -@shortname:schema`) filtered the `y`
 * branch only: `x OR (y AND not schema)`, and the hidden entries came back
 * through `x`. Each fragment in its own group is AND'd as a whole. A lone
 * fragment is returned as it is, so a search with nothing to add sends the
 * same text as before.
 *
 * Inside each fragment parentheses are balanced first, outside `"…"`: a stray
 * `)` in typed text would otherwise close the group early, and an unclosed
 * `(` would swallow the fragments after it. The server repairs a caller's
 * search the same way before it adds a permission filter
 * (QueryService.BalanceParens).
 */
export function andSearch(...parts: Array<string | null | undefined>): string {
    const fragments = parts
        .map((part) => (typeof part === "string" ? part.trim() : ""))
        .filter((part) => part.length > 0);
    if (fragments.length <= 1) return fragments[0] ?? "";
    return fragments.map((fragment) => `(${balanceParens(fragment)})`).join(" ");
}

/** Drops unmatched `)` and closes unclosed `(`, leaving quoted text alone. */
export function balanceParens(text: string): string {
    let depth = 0;
    let quoted = false;
    let out = "";
    for (const ch of text) {
        if (ch === '"') {
            quoted = !quoted;
        } else if (!quoted && ch === ")") {
            if (depth === 0) continue;
            depth--;
        } else if (!quoted && ch === "(") {
            depth++;
        }
        out += ch;
    }
    return out + ")".repeat(depth);
}
