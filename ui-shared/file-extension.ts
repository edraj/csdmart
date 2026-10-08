// The extension of a filename, without the dot; "" when there is none.
//
// Matches against the basename, not the whole string. The capture group is
// `[^.]+`, which also matches "/", so running the pattern over a full path let
// a dotted directory with an extensionless file ("/some.dir/file") return the
// path tail — "dir/file" — instead of "". Callers pass a bare payload.body
// filename today so it never bit, but the function is written to take a
// filename and should behave for a path too.
//
// A dotfile (".env") has nothing before the dot and reads as "no extension",
// which is what icon/type selection wants.
export function getFileExtension(filename: string): string {
  const base = filename.slice(filename.lastIndexOf("/") + 1);
  const ext = /^.+\.([^.]+)$/.exec(base);
  return ext === null ? "" : ext[1];
}
