import { describe, expect, it } from "vitest";
import { isThemePreference, nextTheme, readStoredTheme, resolveTheme } from "./theme";

describe("resolveTheme", () => {
  it("returns the explicit preference regardless of the OS", () => {
    expect(resolveTheme("light", true)).toBe("light");
    expect(resolveTheme("dark", false)).toBe("dark");
  });

  it("follows the OS for 'system'", () => {
    expect(resolveTheme("system", true)).toBe("dark");
    expect(resolveTheme("system", false)).toBe("light");
  });
});

describe("readStoredTheme", () => {
  const storage = (value: string | null) => ({ getItem: () => value });

  it("accepts the three known values", () => {
    expect(readStoredTheme(storage("light"))).toBe("light");
    expect(readStoredTheme(storage("dark"))).toBe("dark");
    expect(readStoredTheme(storage("system"))).toBe("system");
  });

  it("falls back to 'system' for nothing, garbage, or a throwing storage", () => {
    expect(readStoredTheme(storage(null))).toBe("system");
    expect(readStoredTheme(storage("blue"))).toBe("system");
    expect(readStoredTheme(null)).toBe("system");
    expect(
      readStoredTheme({
        getItem: () => {
          throw new Error("denied");
        },
      }),
    ).toBe("system");
  });
});

describe("nextTheme / isThemePreference", () => {
  it("cycles light → dark → system → light", () => {
    expect(nextTheme("light")).toBe("dark");
    expect(nextTheme("dark")).toBe("system");
    expect(nextTheme("system")).toBe("light");
  });

  it("guards unknown values", () => {
    expect(isThemePreference("dark")).toBe(true);
    expect(isThemePreference("auto")).toBe(false);
    expect(isThemePreference(1)).toBe(false);
  });
});
