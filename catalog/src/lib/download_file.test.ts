// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { downloadFile } from "@shared/download-file";

describe("downloadFile", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("clicks an anchor for a blob of the given type and name, then revokes the URL", () => {
    const createObjectURL = vi.fn((_blob: Blob) => "blob:test/1");
    const revokeObjectURL = vi.fn();
    Object.assign(window.URL, { createObjectURL, revokeObjectURL });

    const clicked: HTMLAnchorElement[] = [];
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(function (this: HTMLAnchorElement) {
        clicked.push(this);
      });

    downloadFile("a,b\n1,2\n", "export.csv", "text/csv");

    expect(createObjectURL).toHaveBeenCalledOnce();
    const blob = createObjectURL.mock.calls[0][0];
    expect(blob).toBeInstanceOf(Blob);
    expect(blob.type).toBe("text/csv");

    expect(click).toHaveBeenCalledOnce();
    expect(clicked[0].download).toBe("export.csv");
    expect(clicked[0].href).toBe("blob:test/1");

    // The drift catalog's copy had: the object URL was never released.
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:test/1");
    // Revoked only after the click has been dispatched.
    expect(revokeObjectURL.mock.invocationCallOrder[0]).toBeGreaterThan(
      click.mock.invocationCallOrder[0],
    );
  });
});
