import DOMPurify from "dompurify";

/**
 * Sanitize an HTML string before injecting it via Svelte's `{@html ...}` to
 * prevent stored/reflected XSS. Use this around every `{@html}` sink whose
 * content originates from entry payloads, user input, or rendered Markdown.
 *
 * The app is client-rendered (vite.config: `ssr: false`), but a few routes are
 * pre-rendered by the SSG step (`npm run ssg`) in Node, where there is no DOM.
 * In that no-DOM context DOMPurify cannot run, so we return the input unchanged:
 * those build-time pages carry no user payload, and the browser re-renders and
 * sanitizes on hydration.
 *
 * `Promise<string>` is accepted only because `marked()` is typed
 * `string | Promise<string>`; this app never enables async markdown, so the
 * value is always a string at runtime. A non-string input yields "".
 */
export function sanitizeHtml(
  dirty: string | Promise<string> | null | undefined,
): string {
  if (typeof dirty !== "string") return "";
  if (typeof window === "undefined") return dirty;
  return DOMPurify.sanitize(dirty);
}
