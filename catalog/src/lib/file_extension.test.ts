import { describe, expect, it } from "vitest";
import { getFileExtension } from "@shared/file-extension";

// Drives attachment icon/type selection in both SPAs. The contract worth
// pinning is what counts as "no extension", since the regex requires at least
// one character before the dot and at least one after it.
describe("getFileExtension", () => {
  it("returns the extension of a plain filename", () => {
    expect(getFileExtension("report.pdf")).toBe("pdf");
  });

  it("returns only the last extension for a multi-part name", () => {
    expect(getFileExtension("archive.tar.gz")).toBe("gz");
  });

  it("returns '' when there is no extension", () => {
    expect(getFileExtension("README")).toBe("");
    expect(getFileExtension("")).toBe("");
    expect(getFileExtension("trailing.")).toBe("");
  });

  // A dotfile has nothing before the dot, so it reads as "no extension"
  // rather than as an extension named "env".
  it("treats a dotfile as having no extension", () => {
    expect(getFileExtension(".env")).toBe("");
    expect(getFileExtension("/etc/.env")).toBe("");
  });

  it("preserves case rather than normalising it", () => {
    expect(getFileExtension("IMAGE.PNG")).toBe("PNG");
  });

  it("reads the extension from the basename of a path", () => {
    expect(getFileExtension("/some/dir/file.txt")).toBe("txt");
    expect(getFileExtension("v1.2/archive.tar.gz")).toBe("gz");
  });

  // Regression guard for the drift catalog's copy had: `[^.]+` also matches
  // "/", so without taking the basename first this returned "dir/file".
  it("does not mistake a dotted directory for an extension", () => {
    expect(getFileExtension("/some.dir/file")).toBe("");
    expect(getFileExtension("/a.b/c.d/e")).toBe("");
  });
});
