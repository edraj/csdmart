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

/** Path to a value within a JSON structure */
export type JsonPath = (string | number)[];

/** Callback for value updates */
export type OnUpdate = (path: JsonPath, value: JsonValue) => void;

/** Predefined themes */
export const themes: Record<string, JsonTableTheme> = {
  light: {
    themeName: "light",
    pageBg: "#f0f1f4",
    bg: "#ffffff",
    outerBorder: "rgba(0,0,0,0.08)",
    keyColor: "#3d4654",
    idxColor: "#8b95a5",
    rowEven: "transparent",
    rowOdd: "rgba(0,0,0,0.012)",
    rowHover: "rgba(74,111,165,0.06)",
    keyEven: "transparent",
    keyOdd: "rgba(0,0,0,0.012)",
    keyHover: "rgba(74,111,165,0.05)",
    headers: ["#4a6fa5", "#6b8cbe", "#8ba4cc"],
    headerText: "#ffffff",
    string: "#2a7e4f",
    number: "#b5740d",
    boolTrue: "#2486b9",
    boolFalse: "#c4473a",
    nullColor: "#8b95a5",
    text: "#1d2433",
    editBg: "#fffde8",
    editBorder: "#e8c840",
    hoverBg: "rgba(74,111,165,0.05)",
  },
  dark: {
    themeName: "dark",
    pageBg: "#0f1117",
    bg: "#1e2028",
    outerBorder: "rgba(255,255,255,0.08)",
    keyColor: "#8b95b0",
    idxColor: "#636d83",
    rowEven: "transparent",
    rowOdd: "rgba(255,255,255,0.01)",
    rowHover: "rgba(122,162,247,0.06)",
    keyEven: "transparent",
    keyOdd: "rgba(255,255,255,0.01)",
    keyHover: "rgba(122,162,247,0.05)",
    headers: ["#4a6fa5", "#3d5a8a", "#324a72"],
    headerText: "#e2e5ed",
    string: "#7ec6a0",
    number: "#e2b86b",
    boolTrue: "#56b6c2",
    boolFalse: "#e06c75",
    nullColor: "#636d83",
    text: "#c8cdd8",
    editBg: "#2a2820",
    editBorder: "#8a7a30",
    hoverBg: "rgba(122,162,247,0.06)",
  },
};
