// Hands a Blob to the browser as a file download.
//
// Shared by cxb and catalog (CSV export, query/export tools). The two copies
// had drifted: catalog's never revoked the object URL, so every export leaked
// the blob for the lifetime of the page. The URL is revoked right after the
// synthetic click — the browser has already taken ownership of the blob by
// then, so the download is unaffected.
export function downloadFile(data: BlobPart, fileName: string, fileType: string): void {
  const blob = new Blob([data], { type: fileType });
  const url = window.URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.download = fileName;
  a.href = url;
  a.click();
  a.remove();
  window.URL.revokeObjectURL(url);
}

export default downloadFile;
