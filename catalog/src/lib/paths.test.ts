// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import {
  absoluteUrl,
  catalogBreadcrumbs,
  catalogPath,
  decodeSubpath,
  encodeSubpath,
  ROOT_SUBPATH_SEGMENT,
  stripBase,
  withBase,
} from "./paths";
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

describe("encodeSubpath", () => {
  it("dash-joins the segments of an API subpath", () => {
    expect(encodeSubpath("/a/b")).toBe("a-b");
    expect(encodeSubpath("a/b")).toBe("a-b");
    expect(encodeSubpath("/a/b/")).toBe("a-b");
    expect(encodeSubpath("a")).toBe("a");
  });

  it("maps the root subpath to the lone-dash segment", () => {
    expect(encodeSubpath("/")).toBe(ROOT_SUBPATH_SEGMENT);
    expect(encodeSubpath("")).toBe(ROOT_SUBPATH_SEGMENT);
    expect(encodeSubpath(null)).toBe(ROOT_SUBPATH_SEGMENT);
    expect(encodeSubpath(undefined)).toBe(ROOT_SUBPATH_SEGMENT);
  });

  it("never produces the percent-encoding that broke the old links", () => {
    // The bug: encodeURIComponent("/a/b") → "%2Fa%2Fb", which the entry page
    // then "decoded" by replacing dashes and sent to the API verbatim.
    expect(encodeSubpath("/a/b")).not.toContain("%");
  });
});

describe("decodeSubpath", () => {
  it("restores a leading-slash API subpath", () => {
    expect(decodeSubpath("a-b")).toBe("/a/b");
    expect(decodeSubpath("a")).toBe("/a");
  });

  it("maps the lone dash (and nothing) back to the root", () => {
    expect(decodeSubpath("-")).toBe("/");
    expect(decodeSubpath("")).toBe("/");
    expect(decodeSubpath(undefined)).toBe("/");
  });

  it("tolerates the legacy leading-dash encoding", () => {
    expect(decodeSubpath("-a-b")).toBe("/a/b");
  });

  it("round-trips with encodeSubpath", () => {
    for (const p of ["/", "/a", "/a/b", "/a/b/c"]) {
      expect(decodeSubpath(encodeSubpath(p))).toBe(p);
    }
  });
});

describe("catalogPath", () => {
  it("builds the space route", () => {
    expect(catalogPath({ space: "books" })).toBe("/catalogs/books");
  });

  it("builds the folder route with the encoded subpath", () => {
    expect(catalogPath({ space: "books", subpath: "/fiction/scifi" })).toBe(
      "/catalogs/books/fiction-scifi",
    );
    expect(catalogPath({ space: "books", subpath: "/" })).toBe(
      "/catalogs/books/-",
    );
  });

  it("always includes the resource_type segment on an entry route", () => {
    // The old share links stopped at the shortname and appended
    // ?resource_type=..., which matched no route and 404'd.
    expect(
      catalogPath({
        space: "books",
        subpath: "/fiction",
        shortname: "dune",
        resourceType: "content",
      }),
    ).toBe("/catalogs/books/fiction/dune/content");
  });

  it("percent-encodes unsafe characters in a segment", () => {
    expect(
      catalogPath({
        space: "books",
        subpath: "/",
        shortname: "a b",
        resourceType: "content",
      }),
    ).toBe("/catalogs/books/-/a%20b/content");
  });
});

describe("catalogBreadcrumbs", () => {
  it("links every ancestor to its cumulative /catalogs path", () => {
    expect(catalogBreadcrumbs({ space: "books", subpath: "/a/b" })).toEqual([
      { name: "books", path: "/catalogs/books" },
      { name: "a", path: "/catalogs/books/a" },
      { name: "b", path: null },
    ]);
  });

  it("does not repeat the space crumb", () => {
    const names = catalogBreadcrumbs({ space: "books", subpath: "/a" }).map(
      (c) => c.name,
    );
    expect(names.filter((n) => n === "books")).toHaveLength(1);
  });

  it("makes the space itself current at the root", () => {
    expect(catalogBreadcrumbs({ space: "books", subpath: "/" })).toEqual([
      { name: "books", path: null },
    ]);
  });

  it("makes the entry current and the folder navigable", () => {
    expect(
      catalogBreadcrumbs({
        space: "books",
        subpath: "/a",
        shortname: "dune",
        catalogsLabel: "Catalogs",
      }),
    ).toEqual([
      { name: "Catalogs", path: "/catalogs" },
      { name: "books", path: "/catalogs/books" },
      { name: "a", path: "/catalogs/books/a" },
      { name: "dune", path: null },
    ]);
  });
});

describe("withBase", () => {
  it("prefixes an app-relative path with the <base href>", () => {
    setBaseHref("/cat/");
    expect(withBase("/")).toBe("/cat/");
    expect(withBase("/help")).toBe("/cat/help");
    expect(withBase("/catalogs/books")).toBe("/cat/catalogs/books");
  });

  it("is a no-op at the root", () => {
    setBaseHref("/");
    expect(withBase("/help")).toBe("/help");
  });

  it("is a no-op when there is no <base> element", () => {
    expect(withBase("/help")).toBe("/help");
  });
});

describe("absoluteUrl", () => {
  it("joins origin, base and path", () => {
    setBaseHref("/cat/");
    expect(
      absoluteUrl("/catalogs/books/-/dune/content", "https://dmart.example"),
    ).toBe("https://dmart.example/cat/catalogs/books/-/dune/content");
  });
});

describe("stripBase", () => {
  it("removes the base prefix from a real pathname", () => {
    setBaseHref("/cat/");
    expect(stripBase("/cat/reset-password")).toBe("/reset-password");
    expect(stripBase("/cat/reset-password/confirm")).toBe(
      "/reset-password/confirm",
    );
  });

  it("leaves an already-unprefixed path alone", () => {
    setBaseHref("/cat/");
    expect(stripBase("/reset-password")).toBe("/reset-password");
  });

  it("maps the prefix alone to the root path", () => {
    setBaseHref("/cat/");
    expect(stripBase("/cat")).toBe("/");
    expect(stripBase("/cat/")).toBe("/");
  });

  it("does not maul a path that merely starts with the same characters", () => {
    setBaseHref("/cat/");
    expect(stripBase("/catalogs")).toBe("/catalogs");
    expect(stripBase("/catalogs/books")).toBe("/catalogs/books");
  });

  it("is a no-op when the app is deployed at the root", () => {
    setBaseHref("/");
    expect(stripBase("/reset-password")).toBe("/reset-password");
    expect(stripBase("/")).toBe("/");
  });

  it("round-trips with withBase", () => {
    setBaseHref("/cat/");
    expect(stripBase(withBase("/login"))).toBe("/login");
  });
});

describe("isPublicRoute on live pathnames", () => {
  // window.location.pathname always carries the <base> prefix, so comparing
  // it raw against "/reset-password" failed and the reset pages were
  // redirected to /login.
  const cases = ["/reset-password", "/reset-password/confirm", "/register"];

  it("treats the reset routes as public when unprefixed", () => {
    setBaseHref("/");
    for (const path of cases) {
      expect(isPublicRoute(path), path).toBe(true);
    }
  });

  it("treats the reset routes as public once the base prefix is stripped", () => {
    setBaseHref("/cat/");
    for (const path of cases) {
      expect(isPublicRoute(stripBase(`/cat${path}`)), path).toBe(true);
    }
  });

  it("still rejects a protected route", () => {
    setBaseHref("/cat/");
    expect(isPublicRoute(stripBase("/cat/dashboard"))).toBe(false);
  });

  it("does not extend the wildcard past a segment boundary", () => {
    setBaseHref("/");
    expect(isPublicRoute("/reset-password-debug")).toBe(false);
    expect(isPublicRoute("/reset-passwordX")).toBe(false);
  });

  it("still covers the wildcard's own path and its children", () => {
    setBaseHref("/");
    expect(isPublicRoute("/reset-password")).toBe(true);
    expect(isPublicRoute("/reset-password/confirm")).toBe(true);
    expect(isPublicRoute("/reset-password/confirm/deeper")).toBe(true);
  });

  it("lets anonymous visitors reach the public pages, including /community", () => {
    setBaseHref("/");
    for (const path of ["/", "/home", "/community", "/help", "/catalogs/x"]) {
      expect(isPublicRoute(path), path).toBe(true);
    }
  });
});
