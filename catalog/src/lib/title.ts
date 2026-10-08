// Document title per route: "Page · Space · <website.title>".
//
// index.html ships a static <title>Catalog</title> and only four routes ever
// changed it. Every route calls setTitle() from a $effect so the title follows
// the locale, and passes B/C call the same function from their pages.
//
//   $effect(() => setTitle($_("my_profile")));
//   $effect(() => setTitle(entryTitle, spaceName));

import { website } from "@/config";

export const TITLE_SEPARATOR = " · ";

/** Pure: joins the non-empty parts and the site title. */
export function pageTitle(
  parts: ReadonlyArray<string | null | undefined>,
  siteTitle: string | null | undefined = website.title,
): string {
  const clean = [...parts, siteTitle]
    .map((p) => (typeof p === "string" ? p.trim() : ""))
    .filter((p) => p.length > 0);
  return clean.join(TITLE_SEPARATOR);
}

/** Sets document.title to "part · part · website.title". No-op outside a browser. */
export function setTitle(...parts: Array<string | null | undefined>): void {
  if (typeof document === "undefined") return;
  document.title = pageTitle(parts);
}
