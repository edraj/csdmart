using Dmart.ParquetAdapter;

namespace Dmart.Cli;

// `dmart archive <space-folder>` and `dmart unarchive <parquet-file>` —
// Phase C of the migration triple. Packs terminal folders into Parquet
// for cold-storage / inode reduction; round-trips back to the dmart
// export layout via the matching extractor.
//
// "Terminal folder" detection: a folder with a `.dm/<shortname>/`
// child subtree is a candidate. The orchestrator walks the source
// tree, identifies every such folder, and invokes ParquetArchiver
// per-folder in parallel. The output is one `<leaf>.parquet` per
// terminal folder, in --output-dir (default: same path as the
// source, alongside the original folder).
//
// Exit codes:
//   0 — clean run
//   1 — any per-folder failure (counted in the summary)
//   2 — tool error (bad args, source missing)
public static class ArchiveCommand
{
    public static async Task<int> Archive(string[] args)
    {
        string? srcPath = null;
        string? outDir = null;
        int parallel = Math.Min(Environment.ProcessorCount, 8);
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    PrintArchiveHelp();
                    return 0;
                case "--output-dir" when i + 1 < args.Length:
                    outDir = args[++i]; break;
                case "--parallel" when i + 1 < args.Length:
                    parallel = int.TryParse(args[++i], out var p) ? Math.Clamp(p, 1, 32) : parallel; break;
                case "--dry-run":
                    dryRun = true; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"archive: unknown argument '{args[i]}'");
                        PrintArchiveHelp();
                        return 2;
                    }
                    if (srcPath is not null)
                    {
                        Console.Error.WriteLine("archive: only one source path is accepted");
                        return 2;
                    }
                    srcPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(srcPath))
        {
            PrintArchiveHelp();
            return 2;
        }
        if (!Directory.Exists(srcPath))
        {
            Console.Error.WriteLine($"archive: source folder '{srcPath}' does not exist");
            return 2;
        }
        srcPath = Path.GetFullPath(srcPath);

        var terminalFolders = FindTerminalFolders(srcPath).ToList();
        Console.WriteLine($"archive: found {terminalFolders.Count} terminal folder(s) under {srcPath}");
        if (terminalFolders.Count == 0) return 0;

        if (dryRun)
        {
            Console.WriteLine("archive: --dry-run, nothing written. Folders that would be packed:");
            foreach (var f in terminalFolders) Console.WriteLine($"  {f}");
            return 0;
        }

        var outRoot = outDir ?? srcPath;
        Directory.CreateDirectory(outRoot);

        int success = 0, failed = 0;
        long totalBytes = 0;
        var lockObj = new object();
        var archiver = new ParquetArchiver();

        await Parallel.ForEachAsync(terminalFolders,
            new ParallelOptions { MaxDegreeOfParallelism = parallel },
            async (folder, ct) =>
            {
                // <outRoot>/<rel-path>/<leaf>.parquet — keep the same
                // tree shape under the output root, with the leaf
                // folder collapsed into a .parquet file.
                var rel = Path.GetRelativePath(srcPath, folder);
                var leafName = Path.GetFileName(folder);
                var parentRel = Path.GetDirectoryName(rel) ?? "";
                var outPath = Path.Combine(outRoot, parentRel, leafName + ".parquet");
                try
                {
                    var result = await archiver.ArchiveFolderAsync(folder, outPath, ct);
                    lock (lockObj)
                    {
                        success++;
                        totalBytes += result.ParquetBytes;
                        Console.WriteLine($"  packed {result.EntryCount,6:N0} entries → {outPath} ({result.ParquetBytes:N0} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        failed++;
                        Console.Error.WriteLine($"  FAILED {folder}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });

        Console.WriteLine();
        Console.WriteLine($"archive summary: {success} succeeded, {failed} failed, {totalBytes:N0} bytes total");
        return failed == 0 ? 0 : 1;
    }

    public static async Task<int> Unarchive(string[] args)
    {
        string? srcPath = null;
        string? outDir = null;
        int parallel = Math.Min(Environment.ProcessorCount, 8);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    PrintUnarchiveHelp();
                    return 0;
                case "--output-dir" when i + 1 < args.Length:
                    outDir = args[++i]; break;
                case "--parallel" when i + 1 < args.Length:
                    parallel = int.TryParse(args[++i], out var p) ? Math.Clamp(p, 1, 32) : parallel; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"unarchive: unknown argument '{args[i]}'");
                        PrintUnarchiveHelp();
                        return 2;
                    }
                    if (srcPath is not null)
                    {
                        Console.Error.WriteLine("unarchive: only one source path is accepted");
                        return 2;
                    }
                    srcPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(srcPath))
        {
            PrintUnarchiveHelp();
            return 2;
        }

        // Source can be a single .parquet file or a directory containing
        // many. Walk recursively when it's a directory; treat as a single
        // file otherwise.
        var parquetFiles = Directory.Exists(srcPath)
            ? Directory.EnumerateFiles(srcPath, "*.parquet", SearchOption.AllDirectories).ToList()
            : new List<string> { srcPath };
        if (parquetFiles.Count == 0)
        {
            Console.Error.WriteLine($"unarchive: no .parquet files found under '{srcPath}'");
            return 2;
        }

        var srcRoot = Directory.Exists(srcPath) ? Path.GetFullPath(srcPath) : Path.GetDirectoryName(Path.GetFullPath(srcPath))!;
        var outRoot = outDir ?? srcRoot;
        Directory.CreateDirectory(outRoot);

        int success = 0, failed = 0, totalEntries = 0;
        var lockObj = new object();
        var extractor = new ParquetExtractor();

        await Parallel.ForEachAsync(parquetFiles,
            new ParallelOptions { MaxDegreeOfParallelism = parallel },
            async (file, ct) =>
            {
                var rel = Path.GetRelativePath(srcRoot, file);
                // Strip the .parquet extension → terminal folder name.
                var withoutExt = Path.Combine(
                    Path.GetDirectoryName(rel) ?? "",
                    Path.GetFileNameWithoutExtension(rel));
                var targetFolder = Path.Combine(outRoot, withoutExt);
                try
                {
                    var result = await extractor.ExtractToFolderAsync(file, targetFolder, ct);
                    lock (lockObj)
                    {
                        success++;
                        totalEntries += result.EntriesExtracted;
                        Console.WriteLine($"  extracted {result.EntriesExtracted,6:N0} entries → {targetFolder}");
                        foreach (var w in result.Warnings)
                            Console.Error.WriteLine($"    warn: {w}");
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        failed++;
                        Console.Error.WriteLine($"  FAILED {file}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });

        Console.WriteLine();
        Console.WriteLine($"unarchive summary: {success} files, {totalEntries:N0} entries, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---- helpers --------------------------------------------------

    private static IEnumerable<string> FindTerminalFolders(string root)
    {
        // A "terminal folder" is any folder that contains a .dm/
        // subdirectory whose children are entry directories (each with
        // a meta.*.json). We don't require leaf-ness (a folder can
        // have entries AND child subfolders that also have entries —
        // each level is independently archivable). The caller invokes
        // the archiver per folder; nesting is handled by the relative-
        // path preservation in the output tree.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var dmPath = Path.Combine(dir, ".dm");
            if (!Directory.Exists(dmPath)) continue;
            // Skip the .dm subdir itself when walking.
            if (Path.GetFileName(dir) == ".dm") continue;
            // The .dm/ folder must contain at least one entry dir
            // (otherwise it's an empty marker and there's nothing to pack).
            if (!Directory.EnumerateDirectories(dmPath).Any()) continue;
            yield return dir;
        }
        // Also include the root itself if it has a .dm/ with entries.
        var rootDm = Path.Combine(root, ".dm");
        if (Directory.Exists(rootDm) && Directory.EnumerateDirectories(rootDm).Any())
            yield return root;
    }

    private static void PrintArchiveHelp()
    {
        Console.WriteLine("""
            dmart archive — pack dmart terminal folders into Apache Parquet for archival.

            Usage: dmart archive [options] <spaces-folder>

            Walks the source tree, finds every folder that has a .dm/<shortname>/
            entry tree, and packs each one into <leaf>.parquet under --output-dir
            (default: same path as the source). Each parquet file holds one row
            per entry; the row carries the full meta.<rt>.json as an opaque
            string plus separate columns for uuid / shortname / resource_type /
            subpath / created_at / updated_at for DuckDB-style ad-hoc querying.

            Small inlinable body files (.json / .html / .md / .txt / .csv /
            .jsonl up to 1 MB) are inlined into a body_bytes column; larger
            bodies and binary attachments stay on disk under the original
            attachments.<rt>/ folder.

            Round-trips via `dmart unarchive`.

            Options:
              --output-dir <dir>   Where to write .parquet files (default: same as source)
              --parallel <N>       Per-folder parallel workers (default: min(nproc, 8))
              --dry-run            List folders that would be packed; don't write anything
              -h, --help           This help

            Exit codes:
              0 — all folders packed successfully
              1 — at least one folder failed (counted in summary)
              2 — tool error (bad args, source missing)
            """);
    }

    private static void PrintUnarchiveHelp()
    {
        Console.WriteLine("""
            dmart unarchive — extract a Parquet-archived dmart tree back to filesystem layout.

            Usage: dmart unarchive [options] <parquet-file-or-dir>

            Companion to `dmart archive`. Reads one .parquet file (or every
            .parquet under a directory) and reconstructs the .dm/<shortname>/
            entry tree under --output-dir. The output round-trips byte-for-byte
            against the source the archiver consumed, plus or minus
            ordering of fields inside meta.<rt>.json (which dmart import
            doesn't depend on).

            Body files inlined into the parquet are written back next to the
            .dm/ folder under the original filename. Binary attachments are
            expected to still be on disk under attachments.<rt>/ from when
            archive ran; the extractor doesn't try to fabricate them.

            Options:
              --output-dir <dir>   Where to extract (default: same as parquet source)
              --parallel <N>       Per-file parallel workers (default: min(nproc, 8))
              -h, --help           This help

            Exit codes:
              0 — all files extracted successfully
              1 — at least one extraction failed (counted in summary)
              2 — tool error (bad args, source missing)
            """);
    }
}
