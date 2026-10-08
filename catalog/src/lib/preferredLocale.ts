// Picks the locale to start the UI in. Pure so it can be tested without
// localStorage or a navigator.
//
// Precedence:
//   1. a locale the visitor chose earlier (the stored value), if still offered
//   2. the deployment's `default_language`
//   3. the browser's language, matched by primary subtag ("ar-IQ" → "ar")
//   4. "en" (or the first offered language when English is not offered)
//
// The bug this replaces: `JSON.parse(stored || '"en"')` made every first-time
// visitor English, so `default_language` and browser detection never ran.
export function resolvePreferredLocale(input: {
  stored: string | null;
  defaultLanguage: string | undefined;
  languages: Record<string, string> | undefined;
  navigatorLocale: string | null | undefined;
}): string {
  const offered = Object.keys(input.languages ?? {});
  const isOffered = (l: unknown): l is string =>
    typeof l === "string" && offered.includes(l);

  if (input.stored !== null) {
    try {
      const parsed: unknown = JSON.parse(input.stored);
      if (isOffered(parsed)) return parsed;
    } catch {
      // A corrupt stored value is simply ignored.
    }
  }

  if (isOffered(input.defaultLanguage)) return input.defaultLanguage;

  const nav = input.navigatorLocale?.toLowerCase();
  if (nav) {
    const primary = nav.split(/[-_]/)[0];
    const match = offered.find((l) => l.toLowerCase() === primary);
    if (match) return match;
  }

  if (offered.includes("en") || offered.length === 0) return "en";
  return offered[0];
}
