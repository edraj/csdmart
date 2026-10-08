import { describe, expect, it } from "vitest";
import {
    joinSubpath,
    normalizeSubpath,
    parentOf,
    recordSubpath,
    sidebarCacheKey,
    subpathSegments,
    toRouteSubpath,
    trashDestination,
    trashRestoreTarget,
} from "./subpath";

/**
 * These helpers sit between list records and the move/delete/restore requests
 * built from them. The bugs they replace were all spelling bugs: a leading
 * slash shifting `split("/")` indices, dashes from the route leaking into an
 * API call, or a list subpath being sent for a record that lives deeper.
 */
describe("normalizeSubpath", () => {
    it("maps every empty spelling to the root", () => {
        expect(normalizeSubpath("")).toBe("/");
        expect(normalizeSubpath("/")).toBe("/");
        expect(normalizeSubpath("//")).toBe("/");
        expect(normalizeSubpath("-")).toBe("/");
        expect(normalizeSubpath(null)).toBe("/");
        expect(normalizeSubpath(undefined)).toBe("/");
    });

    it("adds a leading slash and drops trailing and doubled ones", () => {
        expect(normalizeSubpath("a/b")).toBe("/a/b");
        expect(normalizeSubpath("/a/b/")).toBe("/a/b");
        expect(normalizeSubpath("/a//b")).toBe("/a/b");
    });

    it("decodes the route spelling", () => {
        expect(normalizeSubpath("a-b")).toBe("/a/b");
        expect(normalizeSubpath("/people-u-trash")).toBe("/people/u/trash");
    });
});

describe("subpathSegments / parentOf / joinSubpath / toRouteSubpath", () => {
    it("splits into segments without the root", () => {
        expect(subpathSegments("/")).toEqual([]);
        expect(subpathSegments("/a/b")).toEqual(["a", "b"]);
        expect(subpathSegments("a-b")).toEqual(["a", "b"]);
    });

    it("walks up one level and stops at the root", () => {
        expect(parentOf("/a/b")).toBe("/a");
        expect(parentOf("/a")).toBe("/");
        expect(parentOf("a")).toBe("/");
        expect(parentOf("/")).toBe("/");
        expect(parentOf("a-b-c")).toBe("/a/b");
    });

    it("joins a child onto any spelling of the parent", () => {
        expect(joinSubpath("/", "b")).toBe("/b");
        expect(joinSubpath("/a", "b")).toBe("/a/b");
        expect(joinSubpath("a", "b")).toBe("/a/b");
        expect(joinSubpath("a-x", "b")).toBe("/a/x/b");
    });

    it("produces the route spelling", () => {
        expect(toRouteSubpath("/a/b")).toBe("a-b");
        expect(toRouteSubpath("/")).toBe("");
        expect(toRouteSubpath("a/b/")).toBe("a-b");
    });
});

describe("recordSubpath", () => {
    const list = "people/u/trash";

    it("prefers the record's own subpath on a non-exact list", () => {
        // Trash lists the whole subtree; the record is two levels deeper.
        expect(recordSubpath({ subpath: "/people/u/trash/space/a" }, list)).toBe(
            "/people/u/trash/space/a",
        );
    });

    it("treats a content record and a folder record the same way", () => {
        // A folder record's subpath is its PARENT, exactly like a content
        // record's — the folder itself is `subpath/shortname`.
        expect(recordSubpath({ subpath: "/a" }, "/a")).toBe("/a");
        expect(recordSubpath({ subpath: "/" }, "/")).toBe("/");
    });

    it("falls back to the list subpath when the record has none", () => {
        expect(recordSubpath({}, list)).toBe("/people/u/trash");
        expect(recordSubpath({ subpath: "" }, list)).toBe("/people/u/trash");
        expect(recordSubpath({ subpath: null }, "/")).toBe("/");
        expect(recordSubpath(null, "")).toBe("/");
    });

    it("normalises both sources", () => {
        expect(recordSubpath({ subpath: "a-b" }, list)).toBe("/a/b");
        expect(recordSubpath({}, "a-b")).toBe("/a/b");
    });
});

describe("trashDestination", () => {
    it("nests the entry's space and subpath under the user's trash", () => {
        expect(trashDestination("u", "space", "/a/b")).toBe("/people/u/trash/space/a/b");
        expect(trashDestination("u", "space", "a-b")).toBe("/people/u/trash/space/a/b");
    });

    it("does not leave a trailing slash for a root-level entry", () => {
        expect(trashDestination("u", "space", "/")).toBe("/people/u/trash/space");
        expect(trashDestination("u", "space", "")).toBe("/people/u/trash/space");
    });
});

describe("trashRestoreTarget", () => {
    it("recovers space and subpath from a leading-slash trash path", () => {
        expect(trashRestoreTarget("/people/u/trash/space/a/b")).toEqual({
            space_name: "space",
            subpath: "/a/b",
        });
    });

    it("recovers them from the no-leading-slash spelling too", () => {
        // This is the case the old `split("/").slice(3)` got wrong: with a
        // leading slash the first element is "", so the space came out as
        // "trash".
        expect(trashRestoreTarget("people/u/trash/space/a")).toEqual({
            space_name: "space",
            subpath: "/a",
        });
    });

    it("maps an entry trashed from the space root back to the root", () => {
        expect(trashRestoreTarget("/people/u/trash/space")).toEqual({
            space_name: "space",
            subpath: "/",
        });
    });

    it("accepts the route spelling", () => {
        expect(trashRestoreTarget("people-u-trash-space-a")).toEqual({
            space_name: "space",
            subpath: "/a",
        });
    });

    it("returns null outside a trash folder or without a space", () => {
        expect(trashRestoreTarget("/people/u/trash")).toBeNull();
        expect(trashRestoreTarget("/a/b")).toBeNull();
        expect(trashRestoreTarget("/")).toBeNull();
        expect(trashRestoreTarget("")).toBeNull();
        expect(trashRestoreTarget("/people/u/other/space")).toBeNull();
    });

    it("round-trips trashDestination", () => {
        const dest = trashDestination("u", "space", "/a/b");
        expect(trashRestoreTarget(dest)).toEqual({ space_name: "space", subpath: "/a/b" });
        const rootDest = trashDestination("u", "space", "/");
        expect(trashRestoreTarget(rootDest)).toEqual({ space_name: "space", subpath: "/" });
    });
});

describe("sidebarCacheKey", () => {
    it("gives writers and readers the same key for both spellings", () => {
        expect(sidebarCacheKey("space", "/a/b")).toBe("space:/a/b");
        expect(sidebarCacheKey("space", "/a-b")).toBe("space:/a/b");
        expect(sidebarCacheKey("space", "a-b")).toBe("space:/a/b");
    });

    it("uses the root for every empty spelling", () => {
        expect(sidebarCacheKey("space", "/")).toBe("space:/");
        expect(sidebarCacheKey("space", "")).toBe("space:/");
        expect(sidebarCacheKey("space", undefined)).toBe("space:/");
    });
});
