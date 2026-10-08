import { describe, expect, it } from "vitest";
import { rowKey } from "./rowKey";

describe("rowKey", () => {
    it("prefers the uuid", () => {
        expect(rowKey({ uuid: "abc", shortname: "x", subpath: "/a" })).toBe("abc");
    });

    it("falls back to the locator plus timestamp", () => {
        expect(
            rowKey({
                resource_type: "content",
                subpath: "/posts",
                shortname: "hello",
                attributes: { timestamp: "2026-10-08T10:00:00" },
            }),
        ).toBe("content|/posts|hello|2026-10-08T10:00:00");
    });

    it("keeps two events on the same entry apart", () => {
        const a = rowKey({ subpath: "/p", shortname: "e", attributes: { timestamp: "t1" } });
        const b = rowKey({ subpath: "/p", shortname: "e", attributes: { timestamp: "t2" } });
        expect(a).not.toBe(b);
    });

    it("tolerates missing pieces", () => {
        expect(rowKey(null)).toBe("");
        expect(rowKey({})).toBe("|||");
        expect(rowKey({ uuid: "", shortname: "s" })).toBe("||s|");
    });
});
