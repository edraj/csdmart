import { describe, expect, it } from "vitest";
import { getFileExtension } from "./getFileExtension";

/**
 * Drives attachment icon/type selection. The contract worth pinning is what
 * counts as "no extension", since the regex requires at least one character
 * before the dot and at least one after it.
 */
describe("getFileExtension", () => {
  it("returns the extension of a plain filename", () => {
    expect(getFileExtension("report.pdf")).toBe("pdf");
  });

  it("returns only the last extension for a multi-part name", () => {
    expect(getFileExtension("archive.tar.gz")).toBe("gz");
  });

  it("returns '' when there is no extension", () => {
    expect(getFileExtension("README")).toBe("");
  });

  // A dotfile has nothing before the dot, so it reads as "no extension"
  // rather than as an extension named "env" — which is the desired outcome
  // for icon selection, but is easy to regress by relaxing the leading `.+`.
  it("treats a dotfile as having no extension", () => {
    expect(getFileExtension(".env")).toBe("");
    expect(getFileExtension(".gitignore")).toBe("");
  });

  it("returns '' for a trailing dot with nothing after it", () => {
    expect(getFileExtension("trailing.")).toBe("");
  });

  it("returns '' for an empty string", () => {
    expect(getFileExtension("")).toBe("");
  });

  it("preserves case rather than normalising it", () => {
    expect(getFileExtension("IMAGE.PNG")).toBe("PNG");
  });

  it("handles a path, not just a bare filename", () => {
    expect(getFileExtension("/some/dir/file.txt")).toBe("txt");
  });

  // Regression guard: `[^.]+` also matches "/", so before the basename was
  // taken first this returned the path tail ("dir/file") instead of "".
  it("does not mistake a dotted directory for an extension", () => {
    expect(getFileExtension("/some.dir/file")).toBe("");
    expect(getFileExtension("/a.b/c.d/e")).toBe("");
  });

  it("reads the extension from the basename when the directory has dots", () => {
    expect(getFileExtension("/some.dir/file.txt")).toBe("txt");
    expect(getFileExtension("v1.2/archive.tar.gz")).toBe("gz");
  });

  it("still treats a dotfile in a directory as having no extension", () => {
    expect(getFileExtension("/etc/.env")).toBe("");
  });
});
