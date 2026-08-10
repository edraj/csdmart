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

  // KNOWN LIMITATION, asserted so a future fix is a deliberate change rather
  // than a surprise: the capture group is `[^.]+`, which happily matches "/".
  // A path whose *directory* contains a dot and whose *filename* has none
  // yields the tail of the path instead of "". Latent today — both callers
  // (Attachments.svelte, ModalViewAttachments.svelte) pass a bare
  // `payload.body` filename, never a path. Taking the basename first would
  // fix it.
  it("mishandles a dotted directory with an extensionless file", () => {
    expect(getFileExtension("/some.dir/file")).toBe("dir/file");
  });
});
