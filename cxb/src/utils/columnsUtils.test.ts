import { describe, expect, it } from "vitest";
import { folderRenderingColsToListCols, isTimestampKey } from "./columnsUtils";

describe("isTimestampKey", () => {
    it("matches the bare and the dotted spellings of a timestamp column", () => {
        expect(isTimestampKey("created_at")).toBe(true);
        expect(isTimestampKey("updated_at")).toBe(true);
        expect(isTimestampKey("attributes.created_at")).toBe(true);
        expect(isTimestampKey("attributes.payload.body.updated_at")).toBe(true);
        expect(isTimestampKey("Attributes.Created_At")).toBe(true);
    });

    it("leaves every other column alone", () => {
        expect(isTimestampKey("shortname")).toBe(false);
        expect(isTimestampKey("attributes.payload.schema_shortname")).toBe(false);
        expect(isTimestampKey("created_at.something")).toBe(false);
        expect(isTimestampKey("")).toBe(false);
        expect(isTimestampKey(null)).toBe(false);
        expect(isTimestampKey(undefined)).toBe(false);
    });
});

describe("folderRenderingColsToListCols", () => {
    it("keys the list columns by the folder column key and splits the width evenly", () => {
        const cols = folderRenderingColsToListCols({
            a: { key: "shortname", name: "Shortname" },
            b: { key: "attributes.created_at", name: "Created" },
        });
        expect(Object.keys(cols)).toEqual(["shortname", "attributes.created_at"]);
        expect(cols["attributes.created_at"]).toEqual({
            path: "attributes.created_at",
            title: "Created",
            type: "string",
            width: "50%",
        });
    });

    it("does not divide by zero for an empty definition", () => {
        expect(folderRenderingColsToListCols({})).toEqual({});
    });
});
