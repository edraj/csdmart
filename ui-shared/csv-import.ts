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
}

// Each app passes its own SDK's axios instance: the workspaces pin different
// tsdmart versions, so the shared module must not import one itself (see
// password-reset.ts). tsdmart's resourcesFromCsv can't send `start_row`.
export type CsvPost = (
  url: string,
  body: FormData,
  config: { params: Record<string, string | number | boolean> },
) => Promise<{ data: unknown }>;

export async function uploadCsv(post: CsvPost, upload: CsvUpload): Promise<CsvImportResult> {
  // A picked File is a snapshot: once the file on disk changes (say, between an
  // upload and "Continue from row N") it can't be read, and axios would report
  // that as a bare "Network Error" — indistinguishable from the server.
  try {
    await upload.file.slice(0, 1).arrayBuffer();
  } catch {
    return failure("The file can no longer be read; it may have been changed or moved. Select it again and upload.");
  }
  const form = new FormData();
  form.append("resources_file", upload.file);
  const params: Record<string, string | number | boolean> = {};
  if (upload.isUpdate) params.is_update = true;
  if (upload.startRow && upload.startRow > 1) params.start_row = upload.startRow;
  // Same route tsdmart's resourcesFromCsv builds.
  const url = `/managed/resources_from_csv/${upload.resourceType}/${upload.spaceName}/${upload.subpath}/${upload.schema}`;
  try {
    const { data } = await post(url, form, { params });
    return readCsvImportResponse(data);
  } catch (error) {
    return readCsvImportError(error);
  }
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
