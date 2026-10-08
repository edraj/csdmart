import { ResourceType } from "@edraj/tsdmart/dmart.model";
import {
  isJsonObject,
  type AttachmentsMap,
  type EntryPayload,
  type EntryRecord,
  type LocalizedText,
} from "@/lib/types";

/**
 * Whatever a page has in hand for an entry: a retrieved entry (meta at the
 * top level) or a query record (meta under `attributes`). Every field is
 * read through a check, so any record shape — the SDK's, the app's, a page's
 * own view — can be passed.
 */
export type PostLike = {
  shortname?: unknown;
  owner_shortname?: unknown;
  displayname?: unknown;
  description?: unknown;
  payload?: unknown;
  attributes?: unknown;
  attachments?: unknown;
};

const asText = (value: unknown): string | undefined =>
  typeof value === "string" && value ? value : undefined;

const asLocalized = (value: unknown): LocalizedText | null =>
  isJsonObject(value) ? (value as LocalizedText) : null;

const asPayload = (value: unknown): EntryPayload | null =>
  isJsonObject(value) ? (value as EntryPayload) : null;

/** The `attributes` of a query record, or null for a flat entry. */
const attributesOf = (item: PostLike): EntryRecord["attributes"] | null =>
  isJsonObject(item.attributes) ? (item.attributes as EntryRecord["attributes"]) : null;

/** The first non-empty string among `values`. */
const firstText = (...values: unknown[]): string | undefined => {
  for (const value of values) {
    const text = asText(value);
    if (text) return text;
  }
  return undefined;
};

/** `payload.body[...path]` when the body is an object, else undefined. */
function bodyField(payload: EntryPayload | null | undefined, ...path: string[]): unknown {
  let cur: unknown = payload?.body;
  for (const key of path) {
    if (!isJsonObject(cur)) return undefined;
    cur = cur[key];
  }
  return cur;
}

export function getDisplayName(item: PostLike, locale?: string): string {
  const shortname = asText(item.shortname) ?? "";
  const displayname = asLocalized(item.displayname);
  if (displayname) {
    return (
      firstText(locale ? displayname[locale] : undefined, displayname.ar, displayname.en) ||
      shortname
    );
  }
  return firstText(bodyField(attributesOf(item)?.payload, "title")) || shortname;
}

export function getDescription(item: PostLike, locale?: string): string {
  const description = asLocalized(item.description);
  if (description) {
    return (
      firstText(locale ? description[locale] : undefined, description.ar, description.en) || ""
    );
  }
  return "";
}

export function getAuthorInfo(item: PostLike, fallback: string): string {
  const relationships = attributesOf(item)?.relationships ?? [];
  const author = relationships.find((rel) => rel.attributes?.role === "editor");
  return author?.related_to?.shortname || asText(item.owner_shortname) || fallback;
}

export function getPostTitle(postData: PostLike | null | undefined): string {
  const title = firstText(bodyField(asPayload(postData?.payload), "title"));
  if (title) return title;
  return getDisplayName(postData ?? {});
}

export function getPostContent(postData: PostLike | null | undefined): string {
  const payload = asPayload(postData?.payload);
  if (!payload?.body) {
    return "";
  }

  const contentType = payload.content_type;
  const body = payload.body;

  if (typeof body === "string") return body;
  if (!isJsonObject(body)) return "";

  const content = asText(body.content);
  if (content) return content;
  if (contentType === "html") return "";

  const entries = Object.entries(body);
  if (entries.length > 0) {
    return entries
      .map(([key, value]) => `**${key}:** ${value}`)
      .join("\n\n");
  }

  return "";
}

export interface CategorizedAttachments {
  reactions: EntryRecord[];
  comments: EntryRecord[];
  mediaFiles: EntryRecord[];
}

const isMediaContentType = (contentType: string): boolean =>
  contentType.startsWith("image/") ||
  contentType.startsWith("video/") ||
  contentType.startsWith("audio/") ||
  contentType === "application/pdf";

export function categorizeAttachments(item: PostLike): CategorizedAttachments {
  const reactions: EntryRecord[] = [];
  const comments: EntryRecord[] = [];
  const mediaFiles: EntryRecord[] = [];

  const attachments = isJsonObject(item.attachments) ? (item.attachments as AttachmentsMap) : null;
  if (attachments) {
    for (const group of Object.values(attachments)) {
      if (!Array.isArray(group)) continue;
      for (const attachment of group) {
        const contentType = attachment.attributes?.payload?.content_type;
        if (attachment.resource_type === ResourceType.reaction) {
          reactions.push(attachment);
        } else if (attachment.resource_type === ResourceType.comment) {
          comments.push(attachment);
        } else if (
          attachment.resource_type === ResourceType.media ||
          (typeof contentType === "string" && isMediaContentType(contentType))
        ) {
          mediaFiles.push(attachment);
        }
      }
    }
  }

  return { reactions, comments, mediaFiles };
}

export function getReactionType(reaction: PostLike | null | undefined): string {
  return (
    firstText(
      bodyField(attributesOf(reaction ?? {})?.payload, "body", "type"),
      bodyField(asPayload(reaction?.payload), "body", "type"),
    ) || "unknown"
  );
}

export function getCommentText(comment: PostLike | null | undefined, fallback: string): string {
  const attributes = attributesOf(comment ?? {});
  return (
    firstText(
      attributes?.displayname?.ar,
      attributes?.displayname?.en,
      bodyField(attributes?.payload, "body"),
      bodyField(asPayload(comment?.payload), "body"),
    ) || fallback
  );
}

export function getCommentState(comment: PostLike | null | undefined): string {
  return (
    firstText(
      bodyField(attributesOf(comment ?? {})?.payload, "state"),
      bodyField(asPayload(comment?.payload), "state"),
    ) || "unknown"
  );
}

export function getReactionEmoji(type: string): string {
  switch (type) {
    case "like":
      return "👍";
    case "love":
      return "❤️";
    default:
      return "❤️";
  }
}

// Breadcrumb building lives in @/lib/paths (catalogBreadcrumbs); the copy that
// used to be here linked to a non-existent /catalog/... prefix.
export type { Breadcrumb } from "@/lib/paths";
