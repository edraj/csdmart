export function getFileExtension(filename: string): string {
    // Match against the basename, not the whole string. The capture group is
    // `[^.]+`, which also matches "/", so running the pattern over a full path
    // let a dotted directory with an extensionless file ("/some.dir/file")
    // return the path tail — "dir/file" — instead of "". Today's callers pass
    // a bare payload.body filename so it never bit, but the function is
    // written to take a filename and should behave for a path too.
    const base = filename.slice(filename.lastIndexOf("/") + 1);
    const ext = /^.+\.([^.]+)$/.exec(base);
    return ext === null ? "" : ext[1];
}
