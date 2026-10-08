/**
 * The shapes the admin UI reads and writes on top of the SDK's record types:
 * the per-resource-type fields a dmart entry carries (which `ResponseEntry`
 * leaves open), a folder's rendering payload, the meta form's data, and an
 * attachment record. Every field is optional because the server strips empty
 * values from its responses — an absent field and an empty one are the same
 * thing to it, and the forms normalise what they need on mount.
 */
import type { ApiResponseRecord, ResourceType, ResponseEntry } from "@edraj/tsdmart";
import type { FolderColumn } from "@/utils/columnsUtils";

/** One translatable field as the meta forms edit it: a cleared box is null. */
export interface MetaTranslation {
    en?: string | null;
    ar?: string | null;
    ku?: string | null;
}

/** What MetaForm edits on every entry; the rest of the entry rides along. */
export interface MetaFormData extends Record<string, unknown> {
    shortname?: string | null;
    is_active?: boolean;
    slug?: string | null;
    displayname?: MetaTranslation;
    description?: MetaTranslation;
}

/** A space entry's own settings (SpaceForm). */
export interface SpaceSettings {
    hide_folders?: string[];
    hide_space?: boolean;
    active_plugins?: string[];
    ordinal?: number;
}

/** A permission entry's rules (MetaPermissionForm and the explorers' maps). */
export interface PermissionRules {
    subpaths?: Record<string, string[]>;
    resource_types?: string[];
    actions?: string[];
    conditions?: string[];
    restricted_fields?: string[];
    allowed_fields_values?: Record<string, unknown>;
    filter_fields_values?: string;
}

/** A permission entry as `retrieveEntry` returns it: the record plus its rules, which the SDK's type leaves open. */
export type PermissionEntry = ResponseEntry & PermissionRules;

/** Where a relationship points. */
export interface RelationshipLocator {
    type: ResourceType;
    space_name: string;
    subpath: string;
    shortname: string;
    schema_shortname?: string;
}

/** One entry of an entry's `relationships`; `attributes` is whatever JSON the editor was given. */
export interface Relationship {
    related_to: RelationshipLocator;
    attributes: unknown;
}

/**
 * The per-type fields the entry editor's forms read and write, beyond the
 * record itself: a space's settings, a permission's rules, a user's roles, a
 * role's permissions, a ticket's workflow state, and the relationships tab.
 */
export interface EntryFields extends SpaceSettings, PermissionRules {
    roles?: string[];
    permissions?: string[];
    workflow_shortname?: string;
    state?: string;
    is_open?: boolean;
    relationships?: Relationship[];
}

/** `query` in a folder's rendering payload: how its list is fetched. */
export interface FolderQuery {
    type?: string;
    search?: string;
    filter_types?: string[];
}

/** The boolean switches in `FolderRendering`, as the folder form lists them. */
export type FolderFlag =
    | "allow_view"
    | "allow_create"
    | "allow_update"
    | "allow_delete"
    | "allow_create_category"
    | "allow_csv"
    | "allow_upload_csv"
    | "use_media"
    | "stream"
    | "expand_children"
    | "disable_filter";

/** The `folder_rendering` payload body: how a folder's list is shown and what it allows. */
export interface FolderRendering extends Record<string, unknown>, Partial<Record<FolderFlag, boolean>> {
    icon?: string;
    icon_closed?: string;
    icon_opened?: string;
    shortname_title?: string;
    index_attributes?: FolderColumn[];
    query?: FolderQuery;
    search_columns?: FolderColumn[];
    csv_columns?: FolderColumn[];
    sort_by?: string;
    sort_type?: string;
    content_resource_types?: string[];
    content_schema_shortnames?: string[];
    workflow_shortnames?: string[];
    enable_pdf_schema_shortnames?: string[];
}

/** An attachment as an entry's `attachments` map lists it: a record that also carries its uuid. */
export type AttachmentRecord = ApiResponseRecord & { uuid?: string };
