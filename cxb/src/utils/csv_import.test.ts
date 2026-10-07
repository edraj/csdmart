import { describe, expect, it, vi } from "vitest";
import {
  describeCsvFailureRow,
  groupCsvFailures,
  mergeCsvImportResults,
  readCsvImportError,
  readCsvImportResponse,
  uploadCsv,
  type CsvImportResult,
} from "@shared/csv-import";

/**
 * Both upload dialogs used to read `attributes.failed_shortnames` — Python
 * dmart's key, which csdmart never sends — so every failed row was invisible
 * and the dialog said "CSV uploaded successfully". And a request the server
 * gave up on produced a generic toast, never the server's own explanation.
 */
describe("readCsvImportResponse", () => {
  it("reads a clean import", () => {
    const r = readCsvImportResponse({
      status: "success",
      attributes: { inserted: 20, failed_count: 0 },
    });
    expect(r).toEqual({ ok: true, imported: 20, failedCount: 0, failed: [] });
  });

  it("reads the per-row failures csdmart actually sends", () => {
    const r = readCsvImportResponse({
      status: "success",
      attributes: {
        inserted: 3,
        failed_count: 2,
        failed: [
          { row: 5, shortname: "7882992719", error: "entry missing", code: 220 },
          {
            row: 7,
            shortname: "7800011845",
            error: "payload failed schema validation: /offers/1: enum: not allowed",
            code: 402,
            key: "offers.1",
            value: "offer-99",
          },
        ],
      },
    });
    expect(r.ok).toBe(true);
    expect(r.imported).toBe(3);
    expect(r.failedCount).toBe(2);
    expect(r.failed).toEqual([
      { row: 5, shortname: "7882992719", error: "entry missing" },
      {
        row: 7,
        shortname: "7800011845",
        error: "payload failed schema validation: /offers/1: enum: not allowed",
        key: "offers.1",
        value: "offer-99",
      },
    ]);
  });

  it("keeps a failure whose shortname the server stripped as empty", () => {
    // A malformed row has no shortname; the server drops the empty key.
    const r = readCsvImportResponse({
      status: "success",
      attributes: { inserted: 0, failed_count: 1, failed: [{ row: 4, error: "expected 2 fields, got 3" }] },
    });
    expect(r.failed).toEqual([{ row: 4, shortname: "", error: "expected 2 fields, got 3" }]);
  });

  it("surfaces a failed answer in the server's own words", () => {
    const r = readCsvImportResponse({
      status: "failed",
      error: { type: "request", code: 202, message: "schema_shortname required" },
    });
    expect(r).toEqual({ ok: false, imported: 0, failedCount: 0, failed: [], message: "schema_shortname required" });
  });

  it("does not mistake an empty body for success", () => {
    // What a server without the timeout fix sends when its time limit cuts an
    // import off: a 200 with nothing in it.
    const r = readCsvImportResponse("");
    expect(r.ok).toBe(false);
    expect(r.message).toMatch(/empty/);
  });
});

describe("readCsvImportError", () => {
  it("reads a time-limit cut-off: message, progress and where to resume", () => {
    const r = readCsvImportError({
      response: {
        status: 504,
        data: {
          status: "failed",
          error: {
            type: "server",
            code: 504,
            message: "Import stopped at row 18,331: it reached the server's 35-second time limit.",
            info: [
              {
                inserted: 18329,
                failed_count: 1,
                failed: [{ row: 12, shortname: "x", error: "entry exists" }],
                resume_row: 18331,
              },
            ],
          },
        },
      },
    });
    expect(r).toEqual({
      ok: false,
      imported: 18329,
      failedCount: 1,
      failed: [{ row: 12, shortname: "x", error: "entry exists" }],
      message: "Import stopped at row 18,331: it reached the server's 35-second time limit.",
      resumeRow: 18331,
    });
  });

  it("has nothing to resume when only the answer was late", () => {
    const r = readCsvImportError({
      response: {
        status: 504,
        data: { status: "failed", error: { code: 504, message: "The import finished, but ran late.", info: [{ inserted: 5, failed_count: 0 }] } },
      },
    });
    expect(r.resumeRow).toBeUndefined();
    expect(r.imported).toBe(5);
  });

  it("explains a proxy's error page instead of a dmart envelope", () => {
    const r = readCsvImportError({ response: { status: 502, data: "<html>Bad Gateway</html>" } });
    expect(r.ok).toBe(false);
    expect(r.message).toContain("HTTP 502");
  });

  it("explains a connection that died without an answer", () => {
    const r = readCsvImportError({ message: "Network Error" });
    expect(r.ok).toBe(false);
    expect(r.message).toContain("Network Error");
  });
});

describe("uploadCsv", () => {
  const file = new Blob(["shortname,name\nr1,one\n"], { type: "text/csv" });

  it("posts the file to the import route with the mode and start row", async () => {
    const post = vi.fn().mockResolvedValue({ data: { status: "success", attributes: { inserted: 1, failed_count: 0 } } });
    const r = await uploadCsv(post, {
      resourceType: "content",
      spaceName: "repro",
      subpath: "btl",
      schema: "offers",
      file,
      isUpdate: true,
      startRow: 18331,
    });

    expect(r.imported).toBe(1);
    const [url, body, config] = post.mock.calls[0];
    expect(url).toBe("/managed/resources_from_csv/content/repro/btl/offers");
    expect(body.get("resources_file")).toBeInstanceOf(Blob);
    expect(config.params).toEqual({ is_update: true, start_row: 18331 });
  });

  it("sends no optional params for a plain create from the top", async () => {
    const post = vi.fn().mockResolvedValue({ data: { status: "success", attributes: { inserted: 1 } } });
    await uploadCsv(post, { resourceType: "content", spaceName: "s", subpath: "p", schema: "k", file, isUpdate: false, startRow: 1 });
    expect(post.mock.calls[0][2].params).toEqual({});
  });

  it("asks for the file again when it can no longer be read", async () => {
    // What a picked File becomes once the file on disk changes — e.g. edited
    // between an upload and "Continue from row N". Sent anyway, axios would
    // report it as a bare "Network Error", blaming the server.
    const stale = new Blob(["shortname\nr1\n"]);
    stale.slice = () =>
      ({ arrayBuffer: () => Promise.reject(new DOMException("changed", "NotReadableError")) }) as unknown as Blob;
    const post = vi.fn();

    const r = await uploadCsv(post, { resourceType: "content", spaceName: "s", subpath: "p", schema: "k", file: stale, isUpdate: false });

    expect(post).not.toHaveBeenCalled();
    expect(r.ok).toBe(false);
    expect(r.message).toMatch(/select it again/i);
  });

  it("turns a rejected request into a result instead of throwing", async () => {
    const post = vi.fn().mockRejectedValue({
      response: { status: 403, data: { status: "failed", error: { code: 401, message: "no create access" } } },
    });
    const r = await uploadCsv(post, { resourceType: "content", spaceName: "s", subpath: "p", schema: "k", file, isUpdate: false });
    expect(r).toMatchObject({ ok: false, message: "no create access" });
  });
});

describe("mergeCsvImportResults", () => {
  it("adds a continued upload onto the part that came before", () => {
    const first: CsvImportResult = {
      ok: false,
      imported: 100,
      failedCount: 1,
      failed: [{ row: 3, shortname: "a", error: "e1" }],
      message: "stopped",
      resumeRow: 102,
    };
    const rest: CsvImportResult = {
      ok: true,
      imported: 50,
      failedCount: 1,
      failed: [{ row: 120, shortname: "b", error: "e2" }],
    };
    expect(mergeCsvImportResults(first, rest)).toEqual({
      ok: true,
      imported: 150,
      failedCount: 2,
      failed: [
        { row: 3, shortname: "a", error: "e1" },
        { row: 120, shortname: "b", error: "e2" },
      ],
    });
  });
});

describe("groupCsvFailures", () => {
  it("groups rows by error message, most common first", () => {
    const groups = groupCsvFailures([
      { row: 1, shortname: "a", error: "entry missing" },
      { row: 2, shortname: "b", error: "schema" },
      { row: 3, shortname: "c", error: "entry missing" },
    ]);
    expect(groups.map((g) => [g.error, g.rows.map((f) => f.row)])).toEqual([
      ["entry missing", [1, 3]],
      ["schema", [2]],
    ]);
  });
});

describe("describeCsvFailureRow", () => {
  it("names the row, the entry and the offending value", () => {
    expect(describeCsvFailureRow({ row: 18331, shortname: "780", error: "x", key: "offers.1", value: "offer-99" }))
      .toBe('row 18,331 · 780 · offers.1 = "offer-99"');
    expect(describeCsvFailureRow({ row: 4, shortname: "", error: "x" })).toBe("row 4");
  });
});
