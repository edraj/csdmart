import { isJsonObject, type IndexAttribute, type JsonObject } from "./types";

/** The query a folder's listing is configured to run. */
export type FolderQuery = {
  type?: string;
  search?: string;
  filter_types?: string[];
  [key: string]: unknown;
};

/**
 * A folder's `payload.body` once the defaults below are applied: every field
 * the FolderForm binds is present with its type. Settings the form does not
 * know about are kept through the index signature.
 */
export type FolderContent = {
  icon: string;
  icon_closed: string;
  icon_opened: string;
  shortname_title: string;

  index_attributes: IndexAttribute[];
  query: FolderQuery;
  search_columns: unknown[];
  csv_columns: unknown[];

  sort_by: string;
  sort_type: string;

  content_resource_types: string[];
  content_schema_shortnames: string[];
  workflow_shortnames: string[];
  enable_pdf_schema_shortnames: string[];

  allow_view: boolean;
  allow_create: boolean;
  allow_update: boolean;
  allow_delete: boolean;
  allow_create_category: boolean;
  allow_csv: boolean;
  allow_upload_csv: boolean;
  use_media: boolean;
  stream: boolean;
  expand_children: boolean;
  disable_filter: boolean;

  [key: string]: unknown;
};

const str = (value: unknown, fallback = ""): string => (typeof value === "string" ? value : fallback);
const bool = (value: unknown, fallback: boolean): boolean => (typeof value === "boolean" ? value : fallback);
const list = <T>(value: unknown): T[] => (Array.isArray(value) ? value : []);

// Defaults applied to a folder's content when its metadata payload is loaded
// into the FolderForm. Each field keeps the stored value when it has the
// field's type, so that an explicit `false` from the server is preserved — a
// previous version of this merge used `||`, which silently overwrote `false`
// values with the default (and never actually filled in `undefined` because
// the spread came after). A stored value of the wrong type (a null, say)
// falls back to the default the same way a missing one does.
export function applyFolderContentDefaults(content: JsonObject | null | undefined): FolderContent {
  const c: JsonObject = content ?? {};
  return {
    ...c,
    icon: str(c.icon),
    icon_closed: str(c.icon_closed),
    icon_opened: str(c.icon_opened),
    shortname_title: str(c.shortname_title),

    index_attributes: list<IndexAttribute>(c.index_attributes),

    query: isJsonObject(c.query)
      ? c.query
      : {
          type: "",
          search: "",
          filter_types: [],
        },

    search_columns: list(c.search_columns),
    csv_columns: list(c.csv_columns),

    sort_by: str(c.sort_by),
    sort_type: str(c.sort_type),

    content_resource_types: list<string>(c.content_resource_types),
    content_schema_shortnames: list<string>(c.content_schema_shortnames),
    workflow_shortnames: list<string>(c.workflow_shortnames),
    enable_pdf_schema_shortnames: list<string>(c.enable_pdf_schema_shortnames),

    allow_view: bool(c.allow_view, true),
    allow_create: bool(c.allow_create, true),
    allow_update: bool(c.allow_update, true),
    allow_delete: bool(c.allow_delete, false),
    allow_create_category: bool(c.allow_create_category, false),
    allow_csv: bool(c.allow_csv, false),
    allow_upload_csv: bool(c.allow_upload_csv, false),
    use_media: bool(c.use_media, false),
    stream: bool(c.stream, false),
    expand_children: bool(c.expand_children, false),
    disable_filter: bool(c.disable_filter, false),
  };
}
