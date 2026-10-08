import { describe, expect, it } from "vitest";
import { resolvePreferredLocale } from "./preferredLocale";

const languages = { ar: "العربية", en: "English", ku: "کوردی" };

describe("resolvePreferredLocale", () => {
  it("honours a stored choice that is still offered", () => {
    expect(
      resolvePreferredLocale({
        stored: JSON.stringify("ku"),
        defaultLanguage: "ar",
        languages,
        navigatorLocale: "en-US",
      }),
    ).toBe("ku");
  });

  it("ignores a stored choice that is no longer offered", () => {
    expect(
      resolvePreferredLocale({
        stored: JSON.stringify("fr"),
        defaultLanguage: "ar",
        languages,
        navigatorLocale: "en-US",
      }),
    ).toBe("ar");
  });

  it("ignores a corrupt stored value", () => {
    expect(
      resolvePreferredLocale({
        stored: "{not json",
        defaultLanguage: "ar",
        languages,
        navigatorLocale: "en-US",
      }),
    ).toBe("ar");
  });

  it("uses default_language for a first-time visitor", () => {
    // The regression: first visits always came out English.
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: "ar",
        languages,
        navigatorLocale: "en-US",
      }),
    ).toBe("ar");
  });

  it("falls back to the browser language when no default is configured", () => {
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: undefined,
        languages,
        navigatorLocale: "ar-IQ",
      }),
    ).toBe("ar");
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: "fr",
        languages,
        navigatorLocale: "KU",
      }),
    ).toBe("ku");
  });

  it("falls back to English last", () => {
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: undefined,
        languages,
        navigatorLocale: "de-DE",
      }),
    ).toBe("en");
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: undefined,
        languages,
        navigatorLocale: null,
      }),
    ).toBe("en");
  });

  it("falls back to the first offered language when English is not offered", () => {
    expect(
      resolvePreferredLocale({
        stored: null,
        defaultLanguage: undefined,
        languages: { ar: "العربية", ku: "کوردی" },
        navigatorLocale: "de",
      }),
    ).toBe("ar");
  });
});
