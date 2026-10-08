// Every locale file must carry exactly the same keys, with the same ICU
// placeholders in each value. A key present in one file only renders raw in
// the others; a dropped `{count}` renders a sentence with a hole in it.
import { describe, expect, it } from "vitest";
import ar from "./ar.json";
import en from "./en.json";
import ku from "./ku.json";

type Messages = { [key: string]: string | Messages };

function flatten(obj: Messages, prefix = "", out: Record<string, string> = {}): Record<string, string> {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "object" && v !== null) flatten(v, key, out);
    else out[key] = String(v);
  }
  return out;
}

function placeholders(value: string): string[] {
  return [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
}

const locales: Record<string, Record<string, string>> = {
  en: flatten(en as Messages),
  ar: flatten(ar as Messages),
  ku: flatten(ku as Messages),
};

describe("i18n parity", () => {
  const enKeys = Object.keys(locales.en).sort();

  it("has a non-trivial English dictionary", () => {
    expect(enKeys.length).toBeGreaterThan(1000);
  });

  for (const name of ["ar", "ku"] as const) {
    describe(name, () => {
      it("has exactly the English key set", () => {
        const keys = Object.keys(locales[name]).sort();
        const missing = enKeys.filter((k) => !(k in locales[name]));
        const extra = keys.filter((k) => !(k in locales.en));
        expect({ missing, extra }).toEqual({ missing: [], extra: [] });
      });

      it("has the same ICU placeholders as English in every value", () => {
        const mismatches = enKeys
          .filter((k) => k in locales[name])
          .filter((k) => placeholders(locales.en[k]).join(",") !== placeholders(locales[name][k]).join(","))
          .map((k) => ({ key: k, en: locales.en[k], [name]: locales[name][k] }));
        expect(mismatches).toEqual([]);
      });

      it("has no empty values", () => {
        const empty = Object.entries(locales[name])
          .filter(([, v]) => v.trim() === "")
          .map(([k]) => k);
        expect(empty).toEqual([]);
      });
    });
  }

  it("keeps English values in Latin script", () => {
    // en.json once carried Arabic sentences under English keys.
    const arabic = Object.entries(locales.en)
      .filter(([, v]) => /[؀-ۿ]/.test(v))
      .map(([k]) => k);
    expect(arabic).toEqual([]);
  });
});
