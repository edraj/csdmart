import { describe, expect, it } from "vitest";
import { limitJsonForDisplay } from "./displayJson";

describe("limitJsonForDisplay", () => {
    it("returns small values untouched", () => {
        const value = { a: 1, b: [1, 2, 3] };
        const result = limitJsonForDisplay(value);
        expect(result.value).toBe(value);
        expect(result.truncated).toBe(false);
        expect(result.droppedRows).toBe(0);
    });

    it("cuts a long top-level array to maxRows and counts what it dropped", () => {
        const rows = Array.from({ length: 250 }, (_, i) => ({ i }));
        const result = limitJsonForDisplay(rows, { maxRows: 100 });
        expect(Array.isArray(result.value)).toBe(true);
        expect((result.value as unknown[]).length).toBe(100);
        expect(result.droppedRows).toBe(150);
        expect(result.truncated).toBe(true);
        expect(rows.length).toBe(250);
    });

    it("cuts the records of a query envelope but keeps the envelope", () => {
        const response = {
            status: "success",
            attributes: { total: 300 },
            records: Array.from({ length: 300 }, (_, i) => ({ shortname: `r${i}` })),
        };
        const result = limitJsonForDisplay(response, { maxRows: 50 });
        const value = result.value as typeof response;
        expect(value.status).toBe("success");
        expect(value.attributes.total).toBe(300);
        expect(value.records.length).toBe(50);
        expect(result.droppedRows).toBe(250);
        expect(response.records.length).toBe(300);
    });

    it("truncates text that is over the byte budget", () => {
        const big = { blob: "x".repeat(10_000) };
        const result = limitJsonForDisplay(big, { maxBytes: 1_000 });
        expect(typeof result.value).toBe("string");
        expect((result.value as string).length).toBe(1_001);
        expect((result.value as string).endsWith("…")).toBe(true);
        expect(result.truncated).toBe(true);
    });

    it("passes a short string through", () => {
        const result = limitJsonForDisplay("plain text");
        expect(result.value).toBe("plain text");
        expect(result.truncated).toBe(false);
    });

    it("never throws on a circular value", () => {
        const circular: Record<string, unknown> = { name: "loop" };
        circular.self = circular;
        const result = limitJsonForDisplay(circular);
        expect(result.truncated).toBe(true);
        expect(typeof result.value).toBe("string");
    });
});
