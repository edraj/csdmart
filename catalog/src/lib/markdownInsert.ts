/**
 * Markdown snippet builders for the editor's insert actions.
 *
 * Kept out of the Svelte component so the output shape is unit-testable: the
 * image-vs-link decision is the whole behaviour, and getting it wrong is
 * invisible until someone attaches a PDF and sees a broken image icon.
 */
import { isImageFile } from "./fileUtils";

/**
 * Markdown for inserting an attachment.
 *
 * Images become inline images; everything else becomes a link, because
 * `![](report.pdf)` renders as a broken image rather than a download.
 *
 * @param label - link/alt text, usually the attachment shortname
 * @param url - the resolved payload URL
 * @param filename - the attachment's stored filename, used only to classify it
 */
export function attachmentMarkdown(
  label: string,
  url: string,
  filename: string,
): string {
  const safeLabel = sanitizeLabel(label);
  return isImageFile(filename) ? `![${safeLabel}](${url})` : `[${safeLabel}](${url})`;
}

/**
 * Keep a label from breaking out of its `[...]` bracket.
 *
 * A shortname containing `]` would terminate the link text early and leave the
 * rest of the name as stray prose — so escape the brackets rather than strip
 * them, which would silently alter the name the author sees.
 */
export function sanitizeLabel(label: string): string {
  return (label ?? "").replace(/\[/g, "\\[").replace(/\]/g, "\\]");
}
