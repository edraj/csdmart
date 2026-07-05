/**
 * Builders for backend search expressions (Dmart.QueryGrammar). Pure /
 * unit-testable.
 *
 * The backend grammar accepts alternation ONLY in the paren-free form
 * `@field:a|b`, and only when every value is free of grammar metacharacters —
 * unquoted `(`/`)` are STRUCTURAL group delimiters, so the superficially
 * natural `@field:(a|b)` tokenizes as an empty-valued selector (silently
 * dropped) plus a free-text term. Values with spaces or metacharacters must
 * use the quoted form `@field:"a b"`, which cannot appear inside a `|`
 * alternation; multiple such values are combined with an explicit OR group:
 * `(@field:"a b" or @field:"c d")`.
 */

// Mirror of SearchExpressionParser.IsSafeForAlternationValue — characters that
// change the parse when interpolated into an unquoted selector value.
const UNSAFE_VALUE_CHARS = /[|:*()[\]{}"'\\@<>=!\s]/;

/** True when `value` can be embedded in a bare `@field:a|b` alternation. */
export function isSafeAlternationValue(value: string): boolean {
  return value.length > 0 && !UNSAFE_VALUE_CHARS.test(value);
}

/**
 * Build a search clause matching `fieldPath` against any of `values`.
 * Returns "" when no representable value remains. Values containing `"`
 * cannot be expressed in the grammar at all (quoted values are `"[^"]*"`)
 * and are dropped.
 */
export function buildFieldFilterClause(
  fieldPath: string,
  values: string[],
): string {
  const representable = values.filter((v) => v.length > 0 && !v.includes('"'));
  if (representable.length === 0) return "";

  if (representable.every(isSafeAlternationValue)) {
    return `@${fieldPath}:${representable.join("|")}`;
  }

  const clauses = representable.map((v) =>
    isSafeAlternationValue(v) ? `@${fieldPath}:${v}` : `@${fieldPath}:"${v}"`,
  );
  return clauses.length === 1 ? clauses[0] : `(${clauses.join(" or ")})`;
}
