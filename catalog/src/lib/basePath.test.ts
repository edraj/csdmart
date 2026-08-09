// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { stripBasePrefix, withBasePrefix } from "./basePath";
import { isPublicRoute } from "./constants";

/** Installs (or replaces) the <base href> the helpers read. */
function setBaseHref(href: string | null): void {
  document.querySelector("base")?.remove();
  if (href === null) return;
  const base = document.createElement("base");
  base.setAttribute("href", href);
  document.head.appendChild(base);
}

afterEach(() => setBaseHref(null));

describe("stripBasePrefix", () => {
  it("removes the base prefix from a real pathname", () => {
    setBaseHref("/cat/");
    expect(stripBasePrefix("/cat/reset-password")).toBe("/reset-password");
    expect(stripBasePrefix("/cat/reset-password/confirm")).toBe(
      "/reset-password/confirm",
    );
  });

  it("leaves an already-unprefixed path alone", () => {
    setBaseHref("/cat/");
    expect(stripBasePrefix("/reset-password")).toBe("/reset-password");
  });

  it("maps the prefix alone to the root path", () => {
    setBaseHref("/cat/");
    expect(stripBasePrefix("/cat")).toBe("/");
    expect(stripBasePrefix("/cat/")).toBe("/");
  });

  it("does not maul a path that merely starts with the same characters", () => {
    setBaseHref("/cat/");
    expect(stripBasePrefix("/catalogs")).toBe("/catalogs");
    expect(stripBasePrefix("/catalogs/books")).toBe("/catalogs/books");
  });

  it("is a no-op when the app is deployed at the root", () => {
    setBaseHref("/");
    expect(stripBasePrefix("/reset-password")).toBe("/reset-password");
    expect(stripBasePrefix("/")).toBe("/");
  });

  it("round-trips with withBasePrefix", () => {
    setBaseHref("/cat/");
    expect(stripBasePrefix(withBasePrefix("/login"))).toBe("/login");
  });
});

describe("isPublicRoute on live pathnames", () => {
  // The bug this guards: window.location.pathname always carries the <base>
  // prefix, so comparing it raw against "/reset-password" failed and the
  // reset pages were redirected to /login.
  const cases = [
    "/reset-password",
    "/reset-password/confirm",
    "/register",
  ];

  it("treats the reset routes as public when unprefixed", () => {
    setBaseHref("/");
    for (const path of cases) {
      expect(isPublicRoute(path), path).toBe(true);
    }
  });

  it("treats the reset routes as public once the base prefix is stripped", () => {
    setBaseHref("/cat/");
    for (const path of cases) {
      expect(isPublicRoute(stripBasePrefix(`/cat${path}`)), path).toBe(true);
    }
  });

  it("still rejects a protected route", () => {
    setBaseHref("/cat/");
    expect(isPublicRoute(stripBasePrefix("/cat/dashboard"))).toBe(false);
  });
});
