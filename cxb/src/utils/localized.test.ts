import { describe, expect, it } from "vitest";
import { localizedText } from "./localized";

describe("localizedText", () => {
    const name = { en: "Documents", ar: "مستندات" };

    it("prefers the active locale, then English, then anything", () => {
        expect(localizedText(name, "docs", "ar")).toBe("مستندات");
        expect(localizedText(name, "docs", "en")).toBe("Documents");
        expect(localizedText({ ar: "فقط" }, "docs", "en")).toBe("فقط");
        expect(localizedText({ fr: "Seulement" }, "docs", "en")).toBe("Seulement");
    });

    it("matches a regional locale to its language", () => {
        expect(localizedText(name, "docs", "ar-EG")).toBe("مستندات");
    });

    it("skips empty strings and falls back", () => {
        expect(localizedText({ en: "", ar: "  " }, "docs", "en")).toBe("docs");
        expect(localizedText(null, "docs")).toBe("docs");
        expect(localizedText(undefined, "docs")).toBe("docs");
        expect(localizedText("", "docs")).toBe("docs");
    });

    it("passes a plain string through", () => {
        expect(localizedText("Plain", "docs", "ar")).toBe("Plain");
    });
});
