import { describe, expect, it } from "vitest";
import { uploadCsv, type CsvPost, type CsvUpload } from "@shared/csv-import";

/**
 * The upload side of the shared CSV module. Everything here is behaviour the
 * dialogs cannot fix locally — `CsvPost`'s config is built inside `uploadCsv`,
 * so if it sends no Authorization, no timeout, or the whole file on a resumed
 * part, neither cxb's dialog nor catalog's can do anything about it.
 */

const HEADER = "shortname,title";
const ROWS = ["r1,a", "r2,b", "r3,c"];
const CRLF = `${HEADER}\r\n${ROWS.join("\r\n")}\r\n`;

interface Sent {
  url: string;
  params: Record<string, string | number | boolean>;
  headers: Record<string, string>;
  timeout: number;
  body: string;
}

// A CsvPost that records what it was handed and then answers `answer` — either
// resolving with a response body or, when `reject` is set, throwing an axios
// -shaped error.
function recorder(answer: unknown, reject = false): { post: CsvPost; sent: Sent[] } {
  const sent: Sent[] = [];
  const post: CsvPost = async (url, body, config) => {
    const file = body.get("resources_file");
    sent.push({
      url,
      params: config.params,
      headers: config.headers,
      timeout: config.timeout,
      body: file instanceof Blob ? await file.text() : String(file),
    });
    if (reject) throw answer;
    return { data: answer };
  };
  return { post, sent };
}

function upload(over: Partial<CsvUpload> = {}): CsvUpload {
  return {
    resourceType: "content",
    spaceName: "products",
    subpath: "items",
    schema: "product",
    file: new Blob([CRLF]),
    isUpdate: false,
    headers: { Authorization: "Bearer token-123" },
    ...over,
  };
}

const ok = { status: "success", attributes: { inserted: 3, failed_count: 0, failed: [] } };

// The REQUEST_TIMEOUT answer: HTTP 504 carrying how far the import got.
function cutOff(resumeRow: number | null, inserted = 2) {
  const progress: Record<string, unknown> = { inserted, failed_count: 0, failed: [] };
  if (resumeRow !== null) progress.resume_row = resumeRow;
  return {
    response: {
      status: 504,
      data: { error: { message: "Import stopped", info: [progress] } },
    },
  };
}

describe("uploadCsv request", () => {
  it("sends the SDK's auth headers", async () => {
    // cxb's axios instance has no request interceptor, so nothing else puts the
    // bearer token on this call. Without it a cross-origin cxb deployment falls
    // back to the auth_token cookie, which is refused cross-site: 401 on every
    // upload, where the tsdmart call it replaced worked.
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload());
    expect(sent[0].headers.Authorization).toBe("Bearer token-123");
  });

  it("does not carry the SDK's JSON content type onto a multipart body", async () => {
    // Dmart.getHeaders() is the bag used for the SDK's JSON calls, so it holds
    // `Content-type: application/json`. Only the browser can name this body —
    // the header has to carry the multipart boundary it generates.
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload({
      headers: { "Content-type": "application/json", Authorization: "Bearer t" },
    }));
    expect(Object.keys(sent[0].headers).map((k) => k.toLowerCase())).not.toContain("content-type");
    expect(sent[0].headers.Authorization).toBe("Bearer t");
  });

  it("outwaits the server's own deadline", async () => {
    // Both apps default `backend_timeout` to 30 s and build their axios
    // instance with it, so the browser aborted before dmart's 35 s 504 — the
    // one answer that carries resume_row — could arrive.
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload());
    expect(sent[0].timeout).toBeGreaterThan(35_000);
  });

  it("sends no resume parameters on a first upload", async () => {
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload());
    expect(sent[0].params.start_row).toBeUndefined();
    expect(sent[0].params.first_row).toBeUndefined();
    expect(sent[0].body).toBe(CRLF);
  });
});

describe("uploadCsv resumed part", () => {
  it("uploads only the header and the rows from that row", async () => {
    // Re-sending the whole file on every part made a resumed import transfer it
    // once per part, and the server re-parse and discard the earlier rows each
    // time. first_row keeps the server counting in the operator's file.
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload({ startRow: 3 }));
    expect(sent[0].body).toBe(`${HEADER}\r\nr3,c\r\n`);
    expect(sent[0].params.first_row).toBe(3);
    expect(sent[0].params.start_row).toBeUndefined();
  });

  it("counts rows the way the server's line reader does", async () => {
    // CsvService reads the upload with ReadLineAsync, which breaks on "\n",
    // "\r\n" AND a lone "\r". A byte scan that only knew "\n" would slice a
    // CR-terminated file at the wrong row and silently import the wrong rows.
    for (const eol of ["\n", "\r\n", "\r"]) {
      const file = new Blob([`${HEADER}${eol}${ROWS.join(eol)}${eol}`]);
      const { post, sent } = recorder(ok);
      await uploadCsv(post, upload({ file, startRow: 2 }));
      expect(sent[0].body).toBe(`${HEADER}${eol}r2,b${eol}r3,c${eol}`);
    }
  });

  it("falls back to the whole file when the row is past its end", async () => {
    // Nothing to slice to, so the server is asked to skip instead — which for a
    // row past the end simply imports nothing, rather than uploading a body
    // whose rows would be renumbered from a row that does not exist.
    const { post, sent } = recorder(ok);
    await uploadCsv(post, upload({ startRow: 99 }));
    expect(sent[0].body).toBe(CRLF);
    expect(sent[0].params.start_row).toBe(99);
    expect(sent[0].params.first_row).toBeUndefined();
  });
});

describe("uploadCsv resume point", () => {
  it("keeps a resume row that advances", async () => {
    const { post } = recorder(cutOff(3), true);
    const result = await uploadCsv(post, upload());
    expect(result.resumeRow).toBe(3);
    expect(result.imported).toBe(2);
  });

  it("drops a resume row that does not advance past the row it started at", async () => {
    // resume_row is lastRow + 1, so a deadline that fires before the first row
    // completes answers with the row this part already started at. Offering
    // "Continue from row 1" there re-imports the whole file — duplicating every
    // auto-shortname row — and the dialog's merge then discards the counts of
    // the part that came before.
    const { post } = recorder(cutOff(1, 0), true);
    const result = await uploadCsv(post, upload());
    expect(result.resumeRow).toBeUndefined();
  });

  it("drops a resume row that does not advance on a resumed part either", async () => {
    const { post } = recorder(cutOff(2), true);
    const result = await uploadCsv(post, upload({ startRow: 2 }));
    expect(result.resumeRow).toBeUndefined();
  });

  it("reports a finished-but-late import as having nothing left to resume", async () => {
    const { post } = recorder(cutOff(null, 3), true);
    const result = await uploadCsv(post, upload());
    expect(result.resumeRow).toBeUndefined();
    expect(result.imported).toBe(3);
  });
});
