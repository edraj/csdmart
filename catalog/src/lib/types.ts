/**
 * Shared type definitions for the catalog app.
 *
 * `@edraj/tsdmart` types the request side and the response envelopes
 * precisely, but leaves a record's `attributes` and a payload `body` as
 * `any`. These are the shapes the app actually reads off a response, named
 * once so services, components and pages agree. The server strips empty
 * strings, arrays and objects from its JSON, so every field it may drop is
 * optional here.
 *
 * The attribute and body shapes are `type` aliases rather than interfaces on
 * purpose: a type literal is assignable to `Record<string, unknown>`, so a
 * page that reads attributes by a dynamic key can treat them as a JSON bag
 * without a cast, while named fields stay typed.
 */
import type { ApiResponse, ResourceType } from "@edraj/tsdmart";

/** A JSON object as the server returns it. */
export type JsonObject = Record<string, unknown>;

/** True for a plain JSON object (not null, not an array). */
export function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * True for anything a key path can descend into: a JSON object, or an array
 * (indexed by its numeric string, as "items.0.name" does). Arrays are read
 * through the same string index as objects, which is why the guard names
 * `JsonObject`.
 */
export function isIndexable(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null;
}

/**
 * Localized text: whichever of `en` / `ar` / `ku` were filled in. Pages pick
 * one with `localized()` from `@/lib/catalogItems`.
 */
export type LocalizedText = Record<string, string | undefined>;

/** A record's `payload`: the body plus how to read it. */
export type EntryPayload = {
  content_type?: string;
  schema_shortname?: string;
  checksum?: string;
  /** A string for text/html/markdown content, a JSON value otherwise. */
  body?: unknown;
  last_validated?: string;
  validation_status?: "valid" | "invalid";
};

/** One entry of a record's `relationships`: a locator plus free attributes. */
export type Relationship = {
  related_to?: {
    /** The resource type, as the relationship editor writes it. */
    type?: string;
    resource_type?: string;
    space_name?: string;
    subpath?: string;
    shortname?: string;
    schema_shortname?: string;
  };
  attributes?: JsonObject;
};

/**
 * A column of a folder listing (`index_attributes` in the folder's body):
 * the attribute key and a label, plain or localized.
 */
export type IndexAttribute = {
  key: string;
  name: string | LocalizedText;
  sortable?: boolean;
};

/**
 * A record's `resource_type` as the enum. The server only ever sends the
 * enum's values; records keep the field as `string` so pages can compare it
 * with literals, and this names the enum where a request needs one.
 */
export function asResourceType(value: string): ResourceType {
  return value as ResourceType;
}

/** The meta attributes every resource type carries. */
export type EntryAttributes = {
  uuid?: string;
  is_active?: boolean;
  displayname?: LocalizedText;
  description?: LocalizedText;
  tags?: string[];
  created_at?: string;
  updated_at?: string;
  owner_shortname?: string;
  slug?: string;
  schema_shortname?: string;
  workflow_shortname?: string;
  state?: string;
  payload?: EntryPayload;
  relationships?: Relationship[];
};

/** A space's attributes (the `spaces` query), including its configuration. */
export type SpaceAttributes = EntryAttributes & {
  hide_space?: boolean;
  hide_folders?: string[];
  ordinal?: number | null;
  icon?: string;
  root_registration_signature?: string;
  primary_website?: string;
  indexing_enabled?: boolean;
  capture_misses?: boolean;
  check_health?: boolean;
  languages?: string[];
  mirrors?: string[];
  active_plugins?: string[];
};

/** A user's attributes (`management/users`). */
export type UserAttributes = EntryAttributes & {
  email?: string;
  msisdn?: string;
  roles?: string[];
  groups?: string[];
  type?: string;
  language?: string;
  social_avatar_url?: string;
  is_email_verified?: boolean;
  is_msisdn_verified?: boolean;
  force_password_change?: boolean;
};

/** Attachments grouped by resource type: `{ media: [...], comment: [...] }`. */
export type AttachmentsMap = Partial<Record<string, EntryRecord[]>>;

/** One record of a query response (`Dmart.query(...).records[i]`). */
export interface EntryRecord<A extends EntryAttributes = EntryAttributes> {
  resource_type: string;
  shortname: string;
  subpath: string;
  uuid?: string;
  /** Set by the cross-space searches, which tag each hit with its space. */
  space_name?: string;
  attributes: A;
  /** Present when the query asked for attachments. */
  attachments?: AttachmentsMap;
}

/**
 * A retrieved entry (`Dmart.retrieveEntry`): the same attributes, flattened
 * to the top level beside the identifying fields.
 */
export type EntryDetail<A extends EntryAttributes = EntryAttributes> = A & {
  shortname: string;
  subpath?: string;
  resource_type?: string;
  attachments?: AttachmentsMap;
};

/**
 * The records of a response as `EntryRecord`s, with the attributes read as
 * `A`. The SDK types a record's attributes as `Record<string, any>`; this is
 * the one place that names the shape a caller reads. The server answers
 * `records: null` (not `[]`) when nothing matched, so this also guards the
 * array.
 */
export function recordsOf<A extends EntryAttributes = EntryAttributes>(
  response: Pick<ApiResponse, "records"> | null | undefined,
): EntryRecord<A>[] {
  return (response?.records ?? []) as EntryRecord<A>[];
}

/** The attachments of one resource type on a record or entry, or `[]`. */
export function attachmentGroup(
  attachments: AttachmentsMap | null | undefined,
  resourceType: string,
): EntryRecord[] {
  const group = attachments?.[resourceType];
  return Array.isArray(group) ? group : [];
}

/** The stored file name of a media attachment (its payload body), or "". */
export function attachmentFilename(attachment: Pick<EntryRecord, "attributes">): string {
  const body = attachment.attributes?.payload?.body;
  return typeof body === "string" ? body : "";
}

/** A payload body that is a JSON object, or null when it is text or absent. */
export function bodyObject(payload: EntryPayload | null | undefined): JsonObject | null {
  const body = payload?.body;
  return isJsonObject(body) ? body : null;
}

/**
 * A payload body read as the shape `T` this app wrote it with (one of the
 * body types below), or null when the body is not an object.
 */
export function bodyAs<T extends JsonObject>(payload: EntryPayload | null | undefined): T | null {
  const body = payload?.body;
  return isJsonObject(body) ? (body as T) : null;
}

// ---------------------------------------------------------------------------
// Payload bodies with a known shape
// ---------------------------------------------------------------------------

/** Body of a template entry (`/templates` in a space). */
export type TemplateBody = {
  title?: string;
  content?: string;
  space_name?: string;
  schema_shortname?: string;
};

/** Body of an entry written from a template: which template, and the values for its placeholders. */
export type TemplateInstanceBody = {
  template?: string;
  data?: JsonObject;
};

/** Body of a direct message, stored in the recipient's protected folder. */
export type DirectMessageBody = {
  content?: string;
  sender?: string;
  receiver?: string;
  message_type?: string;
  timestamp?: string;
};

/** Body of a group-chat entry under `/groups`. */
export type GroupBody = {
  participants?: string[];
  adminIds?: string[];
  createdBy?: string;
  groupType?: string;
};

/** Body of a group message under `/messages`. */
export type GroupMessageBody = {
  sender?: string;
  groupId?: string;
  content?: string;
  messageType?: string;
};

/** Body of a report ticket under `applications/reports`. */
export type ReportBody = {
  entry?: string;
  reported_entry?: string;
  space_name?: string;
  reported_space?: string;
  subpath?: string;
  reported_subpath?: string;
  report_type?: string;
};

// ---------------------------------------------------------------------------
// JSON Schema
// ---------------------------------------------------------------------------

/**
 * A JSON-schema node as dmart stores it: the keywords the app reads, with
 * every other keyword kept through the index signature.
 */
export interface JsonSchemaNode {
  type?: string | string[];
  title?: string;
  description?: string;
  format?: string;
  enum?: unknown[];
  default?: unknown;
  properties?: Record<string, JsonSchemaNode>;
  items?: JsonSchemaNode | JsonSchemaNode[];
  required?: string[];
  additionalProperties?: boolean | JsonSchemaNode;
  allOf?: JsonSchemaNode[];
  oneOf?: JsonSchemaNode[];
  anyOf?: JsonSchemaNode[];
  $ref?: string;
  definitions?: Record<string, JsonSchemaNode>;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  multipleOf?: number;
  [keyword: string]: unknown;
}

/** A property of a schema the dynamic forms can render. */
export interface SchemaProperty {
  type: 'string' | 'number' | 'integer' | 'boolean' | 'array' | 'object';
  title?: string;
  description?: string;
  default?: unknown;
  enum?: unknown[];
  format?: string;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  multipleOf?: number;
  items?: SchemaProperty;
  properties?: Record<string, SchemaProperty>;
  required?: string[];
  additionalProperties?: boolean;
}

/** An object schema the dynamic forms render. */
export interface Schema {
  title?: string;
  description?: string;
  type: string;
  properties: Record<string, SchemaProperty>;
  required?: string[];
  additionalProperties?: boolean;
}

export interface ValidationResult {
  valid: boolean;
  errors: string[];
}
