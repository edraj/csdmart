import { describe, expect, it } from "vitest";
import { DEFAULT_PLANTUML_SERVER, plantUmlSvgUrl } from "./plantUML";

describe("plantUmlSvgUrl", () => {
    it("uses the public server when none is configured", () => {
        expect(plantUmlSvgUrl(undefined, "abc")).toBe(`${DEFAULT_PLANTUML_SERVER}/svg/abc`);
        expect(plantUmlSvgUrl(null, "abc")).toBe(`${DEFAULT_PLANTUML_SERVER}/svg/abc`);
        expect(plantUmlSvgUrl("", "abc")).toBe(`${DEFAULT_PLANTUML_SERVER}/svg/abc`);
        expect(plantUmlSvgUrl("   ", "abc")).toBe(`${DEFAULT_PLANTUML_SERVER}/svg/abc`);
    });

    it("points at the configured server and tolerates trailing slashes", () => {
        expect(plantUmlSvgUrl("https://uml.example.internal/plantuml", "abc")).toBe(
            "https://uml.example.internal/plantuml/svg/abc",
        );
        expect(plantUmlSvgUrl("https://uml.example.internal/plantuml///", "abc")).toBe(
            "https://uml.example.internal/plantuml/svg/abc",
        );
    });
});
