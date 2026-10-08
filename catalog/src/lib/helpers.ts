import {ContentType, ResourceType} from "@edraj/tsdmart";
import { generateUUID } from "@shared/uuid";

/**
 * Truncates a string to 100 characters and adds ellipsis if longer
 * @param str - The string to truncate
 * @returns Truncated string with ellipsis if longer than 100 characters, original string otherwise
 */
export function truncateString(str: string): string {
  return str && str.length > 100 ? str.slice(0, 100) + "..." : str;
}

/**
 * Renders a human-readable state string based on entity state and activity status
 * @param entity - The entity object containing is_active and state properties
 * @returns Human-readable state string (Inactive, Pending, In Progress, Approved, Rejected, or N/A)
 */
export function renderStateString(entity: { is_active?: boolean; state?: string }) {
  if (entity.is_active === false) {
    return "Inactive";
  }
  if (entity.state === "pending") {
    return "Pending";
  }
  if (entity.state === "in_progress") {
    return "In Progress";
  }
  if (entity.state === "approved") {
    return "Approved";
  }
  if (entity.state === "rejected") {
    return "Rejected";
  }
  return "N/A";
}

/**
 * Determines the content type and resource type for a file based on its MIME type
 * @param file - The file to analyze
 * @returns Object containing contentType and resourceType, or null if unsupported file type
 */
export function getFileType(
  file: File
): { contentType: ContentType; resourceType: ResourceType } | null {
  const mimeType = file.type;

  let contentType: ContentType;
  let resourceType: ResourceType;

  if (mimeType.startsWith("image")) {
    contentType = ContentType.image;
    resourceType = ResourceType.media;
  } else if (mimeType.startsWith("audio")) {
    contentType = ContentType.audio;
    resourceType = ResourceType.media;
  } else if (mimeType.startsWith("video")) {
    contentType = ContentType.video;
    resourceType = ResourceType.media;
  } else {
    switch (mimeType) {
      case "application/pdf":
        contentType = ContentType.pdf;
        resourceType = ResourceType.media;
        break;
      case "text/plain":
        contentType = ContentType.text;
        resourceType = ResourceType.media;
        break;
      case "application/json":
        contentType = ContentType.json;
        resourceType = ResourceType.json;
        break;
      default:
        return null;
    }
  }

  return { contentType, resourceType };
}

/**
 * Formats a number according to the specified locale
 * @param number - The number to format
 * @param locale - The locale string (e.g., 'ar' for Arabic, defaults to English)
 * @returns Formatted number string according to locale
 */
export function formatNumber(number: number, locale: string): string {
  if (locale === "ar") {
    return number.toLocaleString("ar-EG");
  }
  return number.toLocaleString("en-US");
}

/**
 * Format number text with proper locale digits
 * @param number - Number to format
 * @param locale - Locale string (e.g., 'ar' for Arabic)
 * @returns Formatted number string
 */
const ARABIC_DIGITS = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];

export function formatNumberInText(number: number, locale: string): string {
  if (locale === "ar") {
    return number.toString().replace(/\d/g, (d) => ARABIC_DIGITS[+d]);
  }
  return number.toString();
}

export function getParentPath(path: string): string {
  if (path === "/") {
    return path;
  }
  const parts = path.split("/");
  parts.pop();
  return parts.join("/") || "/";
}

export const AUTO_UUID_RULE = "auto";

/**
 * Mirrors Python DMart's `Meta.from_record` auto-uuid behavior:
 * when `shortname == "auto"`, the shortname is replaced with the first 8
 * chars of a freshly generated UUID and the UUID is stored in attributes.
 * (models/core.py::Meta.from_record:232 — `str(record.uuid)[:8]`.)
 */
export function resolveAutoShortname(
  shortname: string,
  attributes?: Record<string, any>,
): { shortname: string; uuid: string | null } {
  if (shortname !== AUTO_UUID_RULE) {
    return { shortname, uuid: null };
  }
  const uuid = generateUUID();
  const resolved = uuid.slice(0, 8);
  if (attributes) {
    attributes.uuid = uuid;
  }
  return { shortname: resolved, uuid };
}
