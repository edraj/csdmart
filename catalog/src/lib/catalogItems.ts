// Pure helpers behind the catalog browse pages (spaces, space, folder).
//
// The three pages each carried their own copy of "pick the display name",
// "count the comments" and "cut a preview", and the copies disagreed: one
// preferred Arabic over English, one ignored the locale, one rebuilt a 150
// character preview from JSON.stringify on every render (review #22, perf
// #17). One implementation each, exercised by catalogItems.test.ts.

export type Localized = Record<string, string | null | undefined> | string | null | undefined;

/** A query/retrieve record as the browse pages see it. */
export interface CatalogRecord {
  shortname: string;
  subpath?: string;
  resource_type?: string;
  uuid?: string;
  space_name?: string;
  attributes?: {
    owner_shortname?: string;
    created_at?: string;
    updated_at?: string;
    is_active?: boolean;
    displayname?: Localized;
    description?: Localized;
    tags?: unknown;
    payload?: EntryPayload;
    [key: string]: unknown;
  };
  attachments?: unknown;
}

export interface EntryPayload {
  content_type?: string;
  schema_shortname?: string;
  body?: unknown;
}

export interface AttachmentCounts {
  comments: number;
  reactions: number;
  media: number;
}

export const EMPTY_COUNTS: AttachmentCounts = Object.freeze({ comments: 0, reactions: 0, media: 0 });

/**
 * The text for the active locale, then English, then Arabic, then whatever
 * is first. A plain string is returned as-is; nothing usable gives `fallback`.
 */
export function localized(value: Localized, locale?: string | null, fallback = ""): string {
  if (typeof value === "string") return value.trim() || fallback;
  if (!value || typeof value !== "object") return fallback;
  const order = [locale ?? "", "en", "ar", "ku"];
  for (const key of order) {
    const text = key ? value[key] : undefined;
    if (typeof text === "string" && text.trim()) return text.trim();
  }
  for (const text of Object.values(value)) {
    if (typeof text === "string" && text.trim()) return text.trim();
  }
  return fallback;
}

/** Display name of a record: localized displayname, then a payload title, then the shortname. */
export function itemTitle(item: CatalogRecord, locale?: string | null): string {
  const name = localized(item.attributes?.displayname, locale);
  if (name) return name;
  const body = item.attributes?.payload?.body;
  if (body && typeof body === "object" && !Array.isArray(body)) {
    const title = (body as Record<string, unknown>).title;
    if (typeof title === "string" && title.trim()) return title.trim();
  }
  return item.shortname;
}

/** Localized description, or "". */
export function itemDescription(item: CatalogRecord, locale?: string | null): string {
  return localized(item.attributes?.description, locale);
}

/** The last segment of an API subpath, or null for the root. */
export function folderNameOf(subpath: string | null | undefined): string | null {
  const parts = (subpath ?? "").split("/").filter((p) => p.length > 0);
  return parts.length ? parts[parts.length - 1] : null;
}

/** Tags as a clean list: array or comma-separated string, trimmed, unique, no blanks. */
export function tagsOf(item: Pick<CatalogRecord, "attributes">): string[] {
  const raw = item.attributes?.tags;
  const list: unknown[] = Array.isArray(raw) ? raw : typeof raw === "string" ? raw.split(",") : [];
  const seen = new Set<string>();
  for (const tag of list) {
    if (typeof tag !== "string") continue;
    const clean = tag.trim();
    if (clean) seen.add(clean);
  }
  return [...seen];
}

interface AttachmentLike {
  resource_type?: string;
  attributes?: { payload?: { content_type?: string } };
}

const MEDIA_TYPES = /^(image|video|audio)\//;

/**
 * Comment / reaction / media counts from the `attachments` map a query
 * response already carries (`{ comment: [...], reaction: [...], media: [...] }`
 * keyed by resource type), so a list needs no per-item aggregation request.
 */
export function attachmentCounts(attachments: unknown): AttachmentCounts {
  if (!attachments || typeof attachments !== "object") return EMPTY_COUNTS;
  const counts = { comments: 0, reactions: 0, media: 0 };
  for (const group of Object.values(attachments as Record<string, unknown>)) {
    if (!Array.isArray(group)) continue;
    for (const raw of group) {
      const attachment = raw as AttachmentLike;
      const type = attachment?.resource_type;
      const contentType = attachment?.attributes?.payload?.content_type ?? "";
      if (type === "comment") counts.comments++;
      else if (type === "reaction") counts.reactions++;
      else if (type === "media" || MEDIA_TYPES.test(contentType) || contentType === "application/pdf") counts.media++;
    }
  }
  return counts;
}

/** The reaction the given user left on an entry, or null. */
export function findUserReactionId(attachments: unknown, userShortname: string | null | undefined): string | null {
  if (!userShortname || !attachments || typeof attachments !== "object") return null;
  for (const group of Object.values(attachments as Record<string, unknown>)) {
    if (!Array.isArray(group)) continue;
    for (const raw of group) {
      const a = raw as { resource_type?: string; shortname?: string; attributes?: { owner_shortname?: string } };
      if (a?.resource_type === "reaction" && a.attributes?.owner_shortname === userShortname && a.shortname) {
        return a.shortname;
      }
    }
  }
  return null;
}

const ELLIPSIS = "…";

function stripHtml(html: string): string {
  return html
    .replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, "[$1]")
    .replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, " ")
    .replace(/<[^>]*>/g, " ")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'");
}

function stripMarkdown(md: string): string {
  return md
    .replace(/```[\s\S]*?```/g, " ")
    .replace(/`([^`]*)`/g, "$1")
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/^\s{0,3}#{1,6}\s+/gm, "")
    .replace(/^\s{0,3}>\s?/gm, "")
    .replace(/^\s*[-*+]\s+/gm, "")
    .replace(/(\*\*|__)(.*?)\1/g, "$2")
    .replace(/(\*|_)(.*?)\1/g, "$2")
    .replace(/~~(.*?)~~/g, "$1");
}

function collapse(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

function truncate(text: string, max: number): string {
  if (text.length <= max) return text;
  const cut = text.slice(0, max);
  const lastSpace = cut.lastIndexOf(" ");
  return (lastSpace > max * 0.6 ? cut.slice(0, lastSpace) : cut).trimEnd() + ELLIPSIS;
}

/**
 * A short plain-text preview of an entry body, built once when the list
 * loads: HTML is stripped, Markdown syntax dropped, JSON flattened to
 * "key: value" pairs. "" when there is nothing to show.
 */
export function previewText(payload: EntryPayload | null | undefined, max = 150): string {
  if (!payload) return "";
  const body = payload.body;
  if (body === null || body === undefined) return "";
  const contentType = (payload.content_type ?? "").toLowerCase();

  let text: string;
  if (typeof body === "string") {
    text = contentType === "html" ? stripHtml(body) : stripMarkdown(body);
  } else if (typeof body === "object") {
    const record = body as Record<string, unknown>;
    const content = record.content;
    if (typeof content === "string") {
      text = contentType === "html" || /<[a-z][\s\S]*>/i.test(content) ? stripHtml(content) : stripMarkdown(content);
    } else {
      text = Object.entries(record)
        .filter(([key]) => key !== "title")
        .map(([key, value]) => `${key}: ${typeof value === "object" && value !== null ? JSON.stringify(value) : String(value)}`)
        .join(" · ");
    }
  } else {
    text = String(body);
  }
  return truncate(collapse(text), max);
}

/** Space-page card: an entry is "hot" once it has drawn a few reactions or comments. */
export function isHot(counts: AttachmentCounts): boolean {
  return counts.reactions > 5 || counts.comments > 2;
}
