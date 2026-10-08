/** Theme configuration for JsonTable components */
export interface JsonTableTheme {
  themeName: string;
  pageBg: string;
  bg: string;
  outerBorder: string;
  keyColor: string;
  idxColor: string;
  rowEven: string;
  rowOdd: string;
  rowHover: string;
  keyEven: string;
  keyOdd: string;
  keyHover: string;
  headers: string[];
  headerText: string;
  string: string;
  number: string;
  boolTrue: string;
  boolFalse: string;
  nullColor: string;
  text: string;
  editBg: string;
  editBorder: string;
  hoverBg: string;
}

/** JSON Schema (subset relevant to JsonTable) */
export interface JsonSchema {
  title?: string;
  type?: string;
  properties?: Record<string, JsonSchema>;
  items?: JsonSchema;
}

/** A JSON-compatible primitive value */
export type JsonPrimitive = string | number | boolean | null;

/** A JSON-compatible value */
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };

/** True when `value` is made only of JSON values (what JSON.parse can return). */
export function isJsonValue(value: unknown): value is JsonValue {
  if (value === null) return true;
  switch (typeof value) {
    case "string":
    case "number":
    case "boolean":
      return true;
    case "object":
      return Array.isArray(value) ? value.every(isJsonValue) : Object.values(value).every(isJsonValue);
    default:
      return false;
  }
}

/** Path to a value within a JSON structure */
export type JsonPath = (string | number)[];

/** Callback for value updates */
export type OnUpdate = (path: JsonPath, value: JsonValue) => void;

const tokenTheme = (themeName: string): JsonTableTheme => ({
  themeName,
  // Every colour is a design token, so the table follows light/dark with the page.
  pageBg: "var(--color-surface)",
  bg: "var(--color-surface-2)",
  outerBorder: "var(--color-border)",
  keyColor: "var(--color-text-muted)",
  idxColor: "var(--color-text-faint)",
  rowEven: "transparent",
  rowOdd: "var(--color-surface-3)",
  rowHover: "var(--color-primary-soft)",
  keyEven: "transparent",
  keyOdd: "var(--color-surface-3)",
  keyHover: "var(--color-primary-soft)",
  headers: ["var(--color-primary)", "var(--color-primary-hover)", "var(--color-info)"],
  headerText: "var(--color-text-on-primary)",
  string: "var(--color-success)",
  number: "var(--color-warning)",
  boolTrue: "var(--color-info)",
  boolFalse: "var(--color-danger)",
  nullColor: "var(--color-text-faint)",
  text: "var(--color-text)",
  editBg: "var(--color-warning-soft)",
  editBorder: "var(--color-warning)",
  hoverBg: "var(--color-primary-soft)",
});

/** Predefined themes: both resolve to the design tokens, which already flip in dark mode. */
export const themes: Record<string, JsonTableTheme> = {
  light: tokenTheme("light"),
  dark: tokenTheme("dark"),
};
