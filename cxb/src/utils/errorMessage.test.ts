import { describe, expect, it } from "vitest";
import { errorMessage } from "./errorMessage";

describe("errorMessage", () => {
    it("reads the dmart envelope out of an axios error", () => {
        const axiosError = {
            message: "Request failed with status code 403",
            response: { status: 403, data: { status: "failed", error: { type: "access", code: 401, message: "You don't have permission" } } },
        };
        expect(errorMessage(axiosError)).toBe("You don't have permission");
    });

    it("accepts a string error or a string body", () => {
        expect(errorMessage({ response: { data: { error: "bad request" } } })).toBe("bad request");
        expect(errorMessage({ response: { data: "plain text body" } })).toBe("plain text body");
        expect(errorMessage("already a string")).toBe("already a string");
    });

    it("falls back to the Error message, then status, then the fallback", () => {
        expect(errorMessage(new Error("timeout of 30000ms exceeded"))).toBe("timeout of 30000ms exceeded");
        expect(errorMessage({ response: { status: 502, statusText: "Bad Gateway", data: {} } })).toBe("502 Bad Gateway");
        expect(errorMessage(null, "Something went wrong")).toBe("Something went wrong");
        expect(errorMessage(undefined, "x")).toBe("x");
        expect(errorMessage({}, "x")).toBe("x");
    });

    it("never yields [object Object]", () => {
        expect(errorMessage({ foo: "bar" })).toBe('{"foo":"bar"}');
        expect(errorMessage(42)).toBe("42");
    });
});
