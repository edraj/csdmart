// The CSV upload dialogs (cxb and catalog) read the import's answer through
// here, because both got it wrong in the same two ways:
//
//   * they looked for `attributes.failed_shortnames` — Python dmart's key, which
//     csdmart never sends. It reports `inserted`, `failed_count` and a `failed`
//     list of { row, shortname, error, code, key?, value? }. Every failed row was
//     invisible, and the dialog said "CSV uploaded successfully".
//
//   * a request that failed as a whole got a generic toast, never the server's
//     own message — including the one that matters most: the REQUEST_TIMEOUT
//     cut-off (HTTP 504), whose `error.info[0]` says how far the import got and
//     which `resume_row` continues it. Rows commit one by one, so a cut-off
//     import is PARTIAL, not failed; the dialog has to say so.

export interface CsvRowFailure {
  // Row in the uploaded file, counting from the first row after the header.
  row: number;
  shortname: string;
  // The server's message for this row, as it wrote it.
  error: string;
  // Schema failures name the field (dot path) and the value that failed.
  key?: string;
  value?: unknown;
}

export interface CsvImportResult {
  // Every row was attempted. Rows can still have failed — see `failed`.
  ok: boolean;
  // Rows saved (create) or updated (update).
  imported: number;
  failedCount: number;
  failed: CsvRowFailure[];
  // The server's explanation when the request as a whole did not complete.
  message?: string;
  // Set when the server's time limit stopped the import part-way: pass it back
  // as `start_row` to import the rest.
  resumeRow?: number;
}

export interface CsvUpload {
  resourceType: string;
  spaceName: string;
  subpath: string;
  schema: string;
  file: Blob;
  isUpdate: boolean;
  // 1 = from the first row after the header.
  startRow?: number;
  // The SDK's auth headers, i.e. `Dmart.getHeaders()`. Required: cxb's axios
  // instance deliberately has no request interceptor ("unlike catalog, cxb
  // never had one — the bearer token is handed to the SDK with
  // Dmart.setToken()"), so nothing puts `Authorization` back on a call made
  // through the instance directly. Without it a cxb deployment whose
  // `website.backend` is a different origin falls back to the `auth_token`
  // cookie, which JwtBearerSetup refuses on a cross-site request: every CSV
  // upload answered 401. The same convention as cxb's other direct-axios
  // calls (tools/import.svelte, tools/db_size_info.svelte).
  headers: Record<string, string>;
}

// Each app passes its own SDK's axios instance: the workspaces pin different
// tsdmart versions, so the shared module must not import one itself (see
// password-reset.ts). tsdmart's resourcesFromCsv can't send `start_row`.
export type CsvPost = (
  url: string,
  body: FormData,
  config: {
    params: Record<string, string | number | boolean>;
    headers: Record<string, string>;
    timeout: number;
  },
) => Promise<{ data: unknown }>;

// A CSV import is not an interactive call. dmart stops it at REQUEST_TIMEOUT
// (35 s by default) and ANSWERS with the row to resume from, so the upload has
// to outwait that answer — but both apps build their axios instance with
// `timeout: website.backend_timeout`, which defaults to 30 s in each. The
// browser aborted five seconds BEFORE the 504 that carries `resume_row`, so
// "Continue from row N" could never appear in the one case it exists for.
// Matches the CLI's own ceiling (DmartClient.UploadTimeout); it only guards a
// connection that died without closing.
const UPLOAD_TIMEOUT_MS = 10 * 60 * 1000;

export async function uploadCsv(post: CsvPost, upload: CsvUpload): Promise<CsvImportResult> {
  // A picked File is a snapshot: once the file on disk changes (say, between an
  // upload and "Continue from row N") it can't be read, and axios would report
  // that as a bare "Network Error" — indistinguishable from the server.
  try {
    await upload.file.slice(0, 1).arrayBuffer();
  } catch {
    return failure("The file can no longer be read; it may have been changed or moved. Select it again and upload.");
  }
  const startRow = upload.startRow && upload.startRow > 1 ? upload.startRow : 1;
  // A resumed part sends only the rows it imports: the header, then the file
  // from where that row begins. Re-sending the whole file on every part made a
  // resumed import upload it once per part, and the server re-parse and discard
  // the earlier rows each time. `first_row` says the body starts AT that row, so
  // the rows the server reports stay the operator's own row numbers; the
  // whole-file fallback keeps `start_row`, which makes it skip to there.
  const body = startRow > 1 ? await sliceFromRow(upload.file, startRow) : null;
  const form = new FormData();
  form.append("resources_file", body ?? upload.file);
  const params: Record<string, string | number | boolean> = {};
  if (upload.isUpdate) params.is_update = true;
  if (startRow > 1) params[body ? "first_row" : "start_row"] = startRow;
  // Same route tsdmart's resourcesFromCsv builds.
  const url = `/managed/resources_from_csv/${upload.resourceType}/${upload.spaceName}/${upload.subpath}/${upload.schema}`;
  let result: CsvImportResult;
  try {
    const { data } = await post(url, form, {
      params,
      headers: withoutContentType(upload.headers),
      timeout: UPLOAD_TIMEOUT_MS,
    });
    result = readCsvImportResponse(data);
  } catch (error) {
    result = readCsvImportError(error);
  }
  // The server answers `resume_row = lastRow + 1`. When not one row fit inside
  // the time limit that is the row this part already started at, and offering
  // "Continue from row N" would re-import from there for ever — and, from row 1,
  // re-import the whole file, duplicating every auto-shortname row. Same guard
  // the CLI applies in UploadCsvToEndAsync.
  if (result.resumeRow !== undefined && result.resumeRow <= startRow) delete result.resumeRow;
  return result;
}

// The SDK's header bag is the one it uses for its JSON calls, so it carries
// `Content-type: application/json` (tsdmart's dmart.model.ts spells it with a
// lower-case `t`; HTTP header names are case-insensitive and axios normalises
// them, so it would be sent). This body is multipart, and only the browser can
// name it — the Content-Type has to carry the boundary it generates.
function withoutContentType(headers: Record<string, string>): Record<string, string> {
  return Object.fromEntries(
    Object.entries(headers).filter(([k]) => k.toLowerCase() !== "content-type"),
  );
}

// The upload body for a resumed part: the header line, then the file from the
// byte offset row `startRow` begins at. null when the file has no such row, or
// when it cannot be scanned — the caller then sends the whole file and lets the
// server skip.
//
// Rows are lines: CsvService reads the upload with ReadLineAsync, so the two
// must agree on what ends one. That is "\n", "\r\n" or a lone "\r", exactly as
// StreamReader sees it. Scanning raw bytes is safe because none of those can
// occur inside a UTF-8 multi-byte sequence.
async function sliceFromRow(file: Blob, startRow: number): Promise<Blob | null> {
  const CHUNK = 1 << 16;
  const LF = 0x0a;
  const CR = 0x0d;
  let lines = 0;
  let headerEnd = 0;
  let pendingCr = false;
  const ends = (offset: number): Blob | null => {
    lines++;
    if (lines === 1) headerEnd = offset;
    return lines === startRow ? new Blob([file.slice(0, headerEnd), file.slice(offset)]) : null;
  };
  try {
    for (let consumed = 0; consumed < file.size; consumed += CHUNK) {
      const chunk = new Uint8Array(await file.slice(consumed, consumed + CHUNK).arrayBuffer());
      for (let i = 0; i < chunk.length; i++) {
        const at = consumed + i;
        // A "\r" already seen and this byte is not the "\n" that would have
        // joined it: the line ended before this byte, which may itself start
        // another break.
        if (pendingCr && chunk[i] !== LF) {
          const done = ends(at);
          if (done) return done;
        }
        pendingCr = false;
        if (chunk[i] === LF) {
          const done = ends(at + 1);
          if (done) return done;
        } else if (chunk[i] === CR) {
          pendingCr = true;
        }
      }
    }
  } catch {
    return null;
  }
  // A trailing lone "\r" still ends a line, at end of file — but there is
  // nothing after it to send.
  return null;
}

// A request that got an answer (2xx).
export function readCsvImportResponse(body: unknown): CsvImportResult {
  if (isObject(body) && body.status === "success") {
    const attrs = isObject(body.attributes) ? body.attributes : {};
    const failed = readFailures(attrs.failed);
    return {
      ok: true,
      imported: count(attrs.inserted),
      failedCount: Math.max(count(attrs.failed_count), failed.length),
      failed,
    };
  }
  if (isObject(body) && isObject(body.error)) return readFailure(body.error);
  // A server without the timeout fix answered a cut-off import with a 200 and
  // no body at all — never treat that as success.
  return failure(
    body === "" || body === null || body === undefined
      ? `The server sent an empty answer. ${MAYBE_PARTIAL}`
      : "The server sent an answer the upload dialog could not read.",
  );
}

// A request that was rejected (non-2xx) or never got an answer — an axios error.
export function readCsvImportError(error: unknown): CsvImportResult {
  const response = isObject(error) && isObject(error.response) ? error.response : undefined;
  if (response) {
    const data = response.data;
    if (isObject(data) && isObject(data.error)) return readFailure(data.error);
    // Not a dmart envelope: a reverse proxy's own error page, typically.
    const status = typeof response.status === "number" ? ` HTTP ${response.status}` : "";
    return failure(`The server answered${status} without an explanation. ${MAYBE_PARTIAL}`);
  }
  const reason = isObject(error) && typeof error.message === "string" && error.message
    ? error.message
    : "no answer";
  return failure(`The upload got no answer from the server (${reason}). ${MAYBE_PARTIAL}`);
}

// Adds the answer to a continued upload (start_row) onto what came before, so
// the dialog shows the whole file's outcome rather than only the last part.
export function mergeCsvImportResults(earlier: CsvImportResult, later: CsvImportResult): CsvImportResult {
  return {
    ...later,
    imported: earlier.imported + later.imported,
    failedCount: earlier.failedCount + later.failedCount,
    failed: [...earlier.failed, ...later.failed],
  };
}

// One group per distinct message, most common first: a thousand "entry exists"
// rows read as one line, not a thousand.
export function groupCsvFailures(failed: CsvRowFailure[]): { error: string; rows: CsvRowFailure[] }[] {
  const groups = new Map<string, CsvRowFailure[]>();
  for (const f of failed) {
    const rows = groups.get(f.error);
    if (rows) rows.push(f);
    else groups.set(f.error, [f]);
  }
  return [...groups.entries()]
    .map(([error, rows]) => ({ error, rows }))
    .sort((a, b) => b.rows.length - a.rows.length);
}

export function describeCsvFailureRow(f: CsvRowFailure): string {
  const parts = [`row ${formatCount(f.row)}`];
  if (f.shortname) parts.push(f.shortname);
  if (f.key) parts.push(f.value === undefined ? f.key : `${f.key} = ${JSON.stringify(f.value)}`);
  return parts.join(" · ");
}

// Grouped like the server's messages ("18,331"), whatever the browser locale.
export function formatCount(n: number): string {
  return n.toLocaleString("en-US");
}

const MAYBE_PARTIAL = "Rows may have been imported before it stopped; check the folder before uploading again.";

type Obj = Record<string, unknown>;

function isObject(v: unknown): v is Obj {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function count(v: unknown): number {
  const n = Number(v);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

function failure(message: string): CsvImportResult {
  return { ok: false, imported: 0, failedCount: 0, failed: [], message };
}

// The error envelope. Only the CSV time-limit cut-off puts import progress in
// info[0]; for every other error those keys are absent and read as zero.
function readFailure(error: Obj): CsvImportResult {
  const progress = Array.isArray(error.info) && isObject(error.info[0]) ? error.info[0] : {};
  const failed = readFailures(progress.failed);
  const result: CsvImportResult = {
    ok: false,
    imported: count(progress.inserted),
    failedCount: Math.max(count(progress.failed_count), failed.length),
    failed,
    message: typeof error.message === "string" && error.message ? error.message : "The upload failed.",
  };
  const resumeRow = count(progress.resume_row);
  if (resumeRow > 0) result.resumeRow = resumeRow;
  return result;
}

function readFailures(list: unknown): CsvRowFailure[] {
  if (!Array.isArray(list)) return [];
  return list.filter(isObject).map((f) => {
    const failure: CsvRowFailure = {
      row: count(f.row),
      // The server strips empty strings, so a row without a shortname has no key.
      shortname: typeof f.shortname === "string" ? f.shortname : "",
      error: typeof f.error === "string" && f.error ? f.error : "unknown error",
    };
    if (typeof f.key === "string" && f.key) failure.key = f.key;
    if ("value" in f) failure.value = f.value;
    return failure;
  });
}
