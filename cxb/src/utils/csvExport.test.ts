import { describe, expect, it } from "vitest";
import { buildCsvQuery, CSV_DOWNLOAD_ALL_CAP, csvFileName } from "./csvExport";

const fallback = { space_name: "sp", subpath: "/", type: "search" };

describe("buildCsvQuery", () => {
    const listQuery = {
        space_name: "sp",
        subpath: "/docs",
        type: "search",
        limit: 15,
        offset: 30,
        retrieve_total: true,
        search: "@shortname:x",
    };

    it("never sends the list's paging offset", () => {
        const q = buildCsvQuery(listQuery, { downloadAll: false }, fallback);
        expect(q).not.toHaveProperty("offset");
        expect(q).not.toHaveProperty("retrieve_total");
        // The rest of the list query is preserved.
        expect(q.search).toBe("@shortname:x");
    });

    it("uses the typed limit and dates when not downloading all", () => {
        const q = buildCsvQuery(
            listQuery,
            { downloadAll: false, limit: "40", startDate: "2026-01-01", endDate: "2026-02-01" },
            fallback,
        );
        expect(q.limit).toBe(40);
        expect(q.from_date).toBe("2026-01-01");
        expect(q.to_date).toBe("2026-02-01");
    });

    it("falls back to the cap when no usable limit is given", () => {
        expect(buildCsvQuery({ ...listQuery, limit: 15 }, { downloadAll: false, limit: "" }, fallback).limit).toBe(15);
        expect(buildCsvQuery({ ...listQuery, limit: 0 }, { downloadAll: false }, fallback).limit).toBe(CSV_DOWNLOAD_ALL_CAP);
        expect(buildCsvQuery(listQuery, { downloadAll: false, limit: "abc" }, fallback).limit).toBe(15);
        expect(buildCsvQuery(listQuery, { downloadAll: false, limit: "-3" }, fallback).limit).toBe(15);
    });

    it("download all uses the list total when known, else the cap, and drops dates", () => {
        const withTotal = buildCsvQuery(
            { ...listQuery, from_date: "2026-01-01" },
            { downloadAll: true, total: 1234, limit: "5", startDate: "2026-01-01" },
            fallback,
        );
        expect(withTotal.limit).toBe(1234);
        expect(withTotal).not.toHaveProperty("from_date");
        expect(withTotal).not.toHaveProperty("to_date");

        expect(buildCsvQuery(listQuery, { downloadAll: true, total: 0 }, fallback).limit).toBe(CSV_DOWNLOAD_ALL_CAP);
        expect(buildCsvQuery(listQuery, { downloadAll: true, total: -1 }, fallback).limit).toBe(CSV_DOWNLOAD_ALL_CAP);
        expect(buildCsvQuery(listQuery, { downloadAll: true }, fallback).limit).toBe(CSV_DOWNLOAD_ALL_CAP);
    });

    it("fills in space, subpath and type when the list query lacks them", () => {
        const q = buildCsvQuery({}, { downloadAll: true }, fallback);
        expect(q.space_name).toBe("sp");
        expect(q.subpath).toBe("/");
        expect(q.type).toBe("search");
    });

    it("does not mutate the input", () => {
        const input = { ...listQuery };
        buildCsvQuery(input, { downloadAll: true }, fallback);
        expect(input).toEqual(listQuery);
    });
});

describe("csvFileName", () => {
    it("keeps the file name free of slashes", () => {
        expect(csvFileName("sp", "/docs/2026")).toBe("sp_docs-2026.csv");
        expect(csvFileName("sp", "docs")).toBe("sp_docs.csv");
    });

    it("names a root export after the space alone", () => {
        expect(csvFileName("sp", "/")).toBe("sp.csv");
        expect(csvFileName("sp", "")).toBe("sp.csv");
        expect(csvFileName("sp", undefined)).toBe("sp.csv");
    });
});
