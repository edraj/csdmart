using System.Security.Cryptography;

namespace Dmart.DataAdapters.Parquet;

// Content-addressed blob storage for attachment media — design §4.3.
//
// `blobs/<sha256[0:2]>/<sha256>`. The two-character prefix exists because a
// single flat directory with a million entries is slow to list and, on some
// filesystems, slow to open a file in. 256 subdirectories is the conventional
// answer and costs nothing.
//
// Content addressing is not a filing convention here, it is the mechanism that
// makes increments cheap:
//
//   * within one export, the same file attached to twenty entries is stored once
//   * across exports, an attachment whose metadata changed but whose bytes did
//     not ships zero blob bytes — the row's media_sha256 simply points at a
//     blob the target already has
//
// The name IS the checksum, so a corrupted or truncated blob is detectable by
// rehashing rather than by trusting a size field.
internal static class BlobStore
{
    public const string DirectoryName = "blobs";

    /// <summary>Writes a blob if it is not already present. Returns its sha256.</summary>
    /// <remarks>
    /// Existing content is NOT rewritten: with a content address, a file that is
    /// already there is by definition identical, and rewriting it would turn
    /// deduplication into repeated I/O. That is the whole saving.
    /// </remarks>
    public static string Write(string exportDirectory, byte[] bytes)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = PathFor(exportDirectory, sha);

        if (File.Exists(path)) return sha;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temp name and move into place, so an interrupted export
        // cannot leave a half-written file under a name that claims to be the
        // hash of its full contents. A reader would trust that name.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer won the race with identical content. Fine.
            try { File.Delete(temp); } catch { /* best effort */ }
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        return sha;
    }

    /// <summary>Reads a blob back, verifying it against its own name.</summary>
    /// <remarks>
    /// The verification is the point of content addressing and costs one hash
    /// per restored blob. Skipping it would let a truncated or corrupted file
    /// restore silently as attachment media, which is undetectable afterwards —
    /// the bytes are opaque and nothing downstream checks them.
    /// </remarks>
    public static byte[] Read(string exportDirectory, string sha256)
    {
        var path = PathFor(exportDirectory, sha256);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"blob {sha256} is missing from the archive — the export is incomplete", path);

        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"blob {sha256} hashes to {actual} — the file is corrupt or truncated");

        return bytes;
    }

    public static string PathFor(string exportDirectory, string sha256)
    {
        if (sha256.Length < 2)
            throw new ArgumentException($"'{sha256}' is not a sha256", nameof(sha256));
        return Path.Combine(exportDirectory, DirectoryName, sha256[..2], sha256);
    }
}
