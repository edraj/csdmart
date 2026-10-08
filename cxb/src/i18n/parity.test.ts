import { describe, expect, it } from "vitest";
import en from "./en.json";
import ar from "./ar.json";

// Every bundled locale must carry exactly the same keys, and each message must
// interpolate exactly the same ICU arguments — otherwise a string silently
// falls back to English (or throws at format time) in one language only.

type Messages = Record<string, string>;
const locales: Record<string, Messages> = { en, ar };

/** Top-level ICU argument names: `{count}`, `{count, plural, ...}`, `{ts, date, full}`. */
function placeholders(message: string): string[] {
    const names = new Set<string>();
    let depth = 0;
    let current = "";
    for (const ch of message) {
        if (ch === "{") {
            depth++;
            if (depth === 1) current = "";
            continue;
        }
        if (ch === "}") {
            if (depth === 1 && current) {
                names.add(current.split(",")[0].trim());
            }
            depth = Math.max(0, depth - 1);
            continue;
        }
        if (depth === 1) current += ch;
    }
    return [...names].sort();
}

describe("locale parity", () => {
    it("en and ar define the same keys", () => {
        const enKeys = Object.keys(en).sort();
        const arKeys = Object.keys(ar).sort();
        expect(arKeys).toEqual(enKeys);
    });

    it("every message is a non-empty string", () => {
        for (const [code, messages] of Object.entries(locales)) {
            for (const [key, value] of Object.entries(messages)) {
                expect(typeof value, `${code}.${key}`).toBe("string");
                expect(value.trim().length, `${code}.${key} is empty`).toBeGreaterThan(0);
            }
        }
    });

    it("en and ar use the same ICU placeholders in each message", () => {
        for (const key of Object.keys(en)) {
            expect(placeholders(ar[key as keyof typeof ar] ?? ""), key).toEqual(placeholders(en[key as keyof typeof en]));
        }
    });

    it("placeholder names are legal ICU argument names", () => {
        // A dot or a space inside `{...}` is pattern syntax, not part of a name;
        // `{data.subpath}` used to throw at format time.
        for (const [code, messages] of Object.entries(locales)) {
            for (const [key, value] of Object.entries(messages)) {
                for (const name of placeholders(value)) {
                    expect(name, `${code}.${key}: {${name}}`).toMatch(/^[A-Za-z0-9_]+$/);
                }
            }
        }
    });

    it("has no obviously untranslated Arabic (an English sentence left as the value)", () => {
        // Values that are code-like identifiers (UUID, N/A, imx.sh) are fine;
        // anything with two or more Latin words and no Arabic letter is not.
        const latinSentence = /^[A-Za-z][a-z]+(?:\s+[A-Za-z][a-z]+){1,}/;
        const offenders = Object.entries(ar)
            .filter(([, v]) => latinSentence.test(v) && !/[؀-ۿ]/.test(v))
            .map(([k]) => k);
        expect(offenders).toEqual([]);
    });
});
