// Audit dist chunks: direct vs transitive vs orphan, with importer chain
// and gzipped size. See audit-deps.sh for usage.
//
// node admin_scripts/audit-deps.mjs <ui-dir>

import { readdirSync, readFileSync, statSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { gzipSync } from "node:zlib";

const ui = process.argv[2];
if (!ui) {
    console.error("usage: audit-deps.mjs <cxb|catalog>");
    process.exit(2);
}

const repoRoot = resolve(".");
const distDir = join(repoRoot, ui, "dist/client");
const pkgJsonPath = join(repoRoot, ui, "package.json");
const nodeModulesDir = join(repoRoot, "node_modules");

// Walk dist/client recursively for every .js file. cxb flat-emits chunks
// into assets/, catalog nests them under assets/js/ via its custom
// rollupOptions.output.assetFileNames pattern.
function walkJs(dir) {
    const out = [];
    const stack = [dir];
    while (stack.length) {
        const d = stack.pop();
        let entries;
        try { entries = readdirSync(d, { withFileTypes: true }); } catch { continue; }
        for (const ent of entries) {
            const p = join(d, ent.name);
            if (ent.isDirectory()) stack.push(p);
            else if (ent.isFile() && ent.name.endsWith(".js")) out.push(p);
        }
    }
    return out;
}

const pkg = JSON.parse(readFileSync(pkgJsonPath, "utf8"));
const directDeps = new Set([
    ...Object.keys(pkg.dependencies || {}),
    ...Object.keys(pkg.devDependencies || {}),
    ...Object.keys(pkg.peerDependencies || {}),
]);

// catalog/vite.config.ts and cxb/vite.config.ts both name chunks after
// the first node_modules path segment (`pkg`) for non-skipped packages.
// For scoped packages this is `@scope` (no sub-package), so multiple
// `@scope/*` packages share a chunk.
function nameLooksScoped(name) {
    return name.startsWith("@");
}

// Strip rolldown's `-[hash][.js]` suffix — the hash is exactly 8 chars
// of [A-Za-z0-9_-]. Returns null if filename doesn't match.
function chunkBaseName(filename) {
    const m = filename.match(/^(.+)-([A-Za-z0-9_-]{8})\.js$/);
    return m ? m[1] : null;
}

function fileGzippedSize(path) {
    const buf = readFileSync(path);
    return gzipSync(buf).length;
}

function fileRawSize(path) {
    return statSync(path).size;
}

// Walk node_modules to build a name → set-of-direct-deps map (recursively
// resolved). For each package in node_modules, read its package.json
// `dependencies` and follow them. Returns a map keyed by package name
// containing every direct dep listed in that package's package.json.
function buildDepGraph() {
    const graph = new Map();
    function readPkg(dir) {
        const pj = join(dir, "package.json");
        if (!existsSync(pj)) return null;
        try { return JSON.parse(readFileSync(pj, "utf8")); }
        catch { return null; }
    }
    function visit(dir) {
        const pj = readPkg(dir);
        if (!pj || !pj.name) return;
        if (graph.has(pj.name)) return;
        const deps = new Set([
            ...Object.keys(pj.dependencies || {}),
            ...Object.keys(pj.peerDependencies || {}),
        ]);
        graph.set(pj.name, deps);
    }
    function walk(dir) {
        let entries;
        try { entries = readdirSync(dir, { withFileTypes: true }); }
        catch { return; }
        for (const ent of entries) {
            if (!ent.isDirectory()) continue;
            const path = join(dir, ent.name);
            if (ent.name.startsWith("@")) {
                // scoped: walk one level deeper for the actual package dirs
                let subs;
                try { subs = readdirSync(path, { withFileTypes: true }); }
                catch { continue; }
                for (const sub of subs) {
                    if (sub.isDirectory()) {
                        visit(join(path, sub.name));
                        // Recurse into nested node_modules too (yarn classic
                        // hoists most stuff, but some packages still nest).
                        const nested = join(path, sub.name, "node_modules");
                        if (existsSync(nested)) walk(nested);
                    }
                }
            } else if (ent.name === ".bin" || ent.name === ".cache") {
                // skip
            } else {
                visit(path);
                const nested = join(path, "node_modules");
                if (existsSync(nested)) walk(nested);
            }
        }
    }
    walk(nodeModulesDir);
    return graph;
}

// BFS from direct deps to discover the closure of reachable packages.
// Returns a Map<pkg, importerChain[]> where importerChain is an example
// path of direct dep -> ... -> pkg.
function buildClosure(graph, directDeps) {
    const closure = new Map();
    const queue = [];
    for (const root of directDeps) {
        closure.set(root, [root]);
        queue.push(root);
    }
    while (queue.length) {
        const pkg = queue.shift();
        const chain = closure.get(pkg);
        const deps = graph.get(pkg);
        if (!deps) continue;
        for (const child of deps) {
            if (closure.has(child)) continue;
            closure.set(child, [...chain, child]);
            queue.push(child);
        }
    }
    return closure;
}

// Scan node_modules to find every package whose path starts with a given
// chunk name. Used for scoped chunks where one chunk = many @scope/*
// packages.
function packagesForChunk(chunkName) {
    if (!nameLooksScoped(chunkName)) {
        return existsSync(join(nodeModulesDir, chunkName)) ? [chunkName] : [];
    }
    const scopeDir = join(nodeModulesDir, chunkName);
    if (!existsSync(scopeDir)) return [];
    return readdirSync(scopeDir, { withFileTypes: true })
        .filter(e => e.isDirectory())
        .map(e => `${chunkName}/${e.name}`);
}

// ── main ─────────────────────────────────────────────────────────────

const graph = buildDepGraph();
const closure = buildClosure(graph, directDeps);

const chunks = walkJs(distDir).map(path => {
    const file = path.slice(distDir.length + 1);
    const basename = file.split("/").pop();
    return {
        file,
        base: chunkBaseName(basename),
        raw: fileRawSize(path),
        gz: fileGzippedSize(path),
    };
});

// Classify each chunk
const direct = []; // chunk maps to a direct dep
const transitive = []; // chunk maps to a transitive dep (with chain)
const orphan = []; // chunk maps to a package not reachable from direct deps
const vendor = []; // chunk is a manualChunks-bucketed vendor (e.g. catalog's `vendor*`)
const userCode = []; // chunk doesn't correspond to a node_modules package

for (const c of chunks) {
    if (!c.base) {
        userCode.push(c); continue;
    }
    // Bucketed vendor chunks (e.g. catalog returns "vendor", "vendor-flowbite",
    // "vendor-routify" from manualChunks) aggregate many packages into one
    // file. We can't decompose them statically, so flag separately.
    if (c.base === "vendor" || c.base.startsWith("vendor-") || c.base === "rolldown-runtime") {
        vendor.push(c); continue;
    }
    const pkgs = packagesForChunk(c.base);
    if (pkgs.length === 0) {
        userCode.push(c); continue;
    }

    // For scoped chunks, "direct" if any sub-package is a direct dep.
    const directHits = pkgs.filter(p => directDeps.has(p));
    const closureHits = pkgs.filter(p => closure.has(p));
    const orphanPkgs = pkgs.filter(p => !closure.has(p));

    if (directHits.length > 0) {
        direct.push({ ...c, pkgs, directHits });
    } else if (closureHits.length > 0) {
        // pick the shortest chain
        const chains = closureHits.map(p => closure.get(p));
        chains.sort((a, b) => a.length - b.length);
        transitive.push({ ...c, pkgs, chain: chains[0] });
    } else {
        orphan.push({ ...c, pkgs: orphanPkgs });
    }
}

// Source-walk: any direct dep declared in <ui>/package.json that isn't
// statically imported anywhere under <ui>/src is potential dead weight in
// package.json (dev-only deps and bin-only tools are common false
// positives — note them but don't flag as errors).
function collectImportSpecifiers(srcDir) {
    const found = new Set();
    const stack = [srcDir];
    const exts = /\.(ts|js|svelte|md|mjs|cjs|tsx|jsx)$/;
    while (stack.length) {
        const d = stack.pop();
        let entries;
        try { entries = readdirSync(d, { withFileTypes: true }); } catch { continue; }
        for (const ent of entries) {
            const p = join(d, ent.name);
            if (ent.isDirectory()) stack.push(p);
            else if (ent.isFile() && exts.test(ent.name)) {
                let code;
                try { code = readFileSync(p, "utf8"); } catch { continue; }
                // Cover: import x from "spec";  import "spec";  import("spec");
                // Skip: relative paths, alias paths starting with @/ or ~/.
                const re = /(?:^|\s|;|\(|=)(?:import\s+(?:[^'"]*?from\s+)?|import\s*\(\s*)['"]([^'"]+)['"]/g;
                let m;
                while ((m = re.exec(code)) !== null) {
                    const spec = m[1];
                    if (spec.startsWith(".") || spec.startsWith("/")) continue;
                    if (spec.startsWith("@/") || spec.startsWith("~/")) continue;
                    // Reduce to package name: "@scope/pkg/sub" -> "@scope/pkg"; "pkg/sub" -> "pkg"
                    const parts = spec.split("/");
                    const name = parts[0].startsWith("@") ? `${parts[0]}/${parts[1] || ""}` : parts[0];
                    found.add(name);
                }
            }
        }
    }
    return found;
}

// Walk src/ AND every config file at the project root that pulls in
// build-time deps (vite.config, svelte.config, tsconfig is JSON so it
// can't import). Without this, things imported only by the build config
// (mdsvex, tailwindcss, vite plugins) get falsely flagged as unused.
const srcDir = join(repoRoot, ui, "src");
const importedFromSrc = existsSync(srcDir) ? collectImportSpecifiers(srcDir) : new Set();
for (const cfg of ["vite.config.ts", "vite.config.js", "vite.config.mjs",
                   "svelte.config.js", "svelte.config.mjs",
                   "babel.config.json", "babel.config.js",
                   "postcss.config.js", "postcss.config.mjs",
                   "eslint.config.mjs", "eslint.config.js"]) {
    const p = join(repoRoot, ui, cfg);
    if (!existsSync(p)) continue;
    let code;
    try { code = readFileSync(p, "utf8"); } catch { continue; }
    const re = /(?:^|\s|;|\(|=|,)(?:import\s+(?:[^'"]*?from\s+)?|import\s*\(\s*|require\s*\(\s*)['"]([^'"]+)['"]/g;
    let m;
    while ((m = re.exec(code)) !== null) {
        const spec = m[1];
        if (spec.startsWith(".") || spec.startsWith("/")) continue;
        const parts = spec.split("/");
        const name = parts[0].startsWith("@") ? `${parts[0]}/${parts[1] || ""}` : parts[0];
        importedFromSrc.add(name);
    }
}
// Node built-ins (and `node:` scheme) aren't package deps — never flag.
const NODE_BUILTINS = new Set([
    "assert", "async_hooks", "buffer", "child_process", "cluster", "console",
    "constants", "crypto", "dgram", "diagnostics_channel", "dns", "domain",
    "events", "fs", "http", "http2", "https", "inspector", "module", "net",
    "os", "path", "perf_hooks", "process", "punycode", "querystring",
    "readline", "repl", "stream", "string_decoder", "sys", "timers", "tls",
    "trace_events", "tty", "url", "util", "v8", "vm", "wasi", "worker_threads",
    "zlib",
]);
function isNodeBuiltin(name) {
    if (name.startsWith("node:")) return true;
    return NODE_BUILTINS.has(name);
}

// "Declared in package.json but never imported by src" — candidates for removal.
const declaredButUnused = [...directDeps].filter(d => !importedFromSrc.has(d)).sort();
// "Imported by src but not declared in package.json" — relying on hoisting.
const importedButUndeclared = [...importedFromSrc]
    .filter(d => !directDeps.has(d) && !isNodeBuiltin(d))
    .sort();

function fmtKB(bytes) { return (bytes / 1024).toFixed(1).padStart(7) + " KB"; }
function totalGz(arr) { return arr.reduce((s, c) => s + c.gz, 0); }

console.log(`${chunks.length} chunks total — ${fmtKB(totalGz(chunks))} gzipped`);
console.log(`  user code         ${userCode.length.toString().padStart(3)} chunks  ${fmtKB(totalGz(userCode))}`);
console.log(`  direct deps       ${direct.length.toString().padStart(3)} chunks  ${fmtKB(totalGz(direct))}`);
console.log(`  transitive deps   ${transitive.length.toString().padStart(3)} chunks  ${fmtKB(totalGz(transitive))}`);
console.log(`  vendor buckets    ${vendor.length.toString().padStart(3)} chunks  ${fmtKB(totalGz(vendor))}  (manualChunks aggregated, can't decompose)`);
console.log(`  orphan deps       ${orphan.length.toString().padStart(3)} chunks  ${fmtKB(totalGz(orphan))}`);
console.log("");

if (orphan.length) {
    console.log(`ORPHANS — bundled but not reachable from any direct dep in ${ui}/package.json:`);
    for (const o of orphan.sort((a, b) => b.gz - a.gz)) {
        console.log(`  ${fmtKB(o.gz)}  ${o.base}  (packages: ${o.pkgs.join(", ")})`);
    }
    console.log("");
}

if (transitive.length) {
    console.log(`TRANSITIVES — top transitive deps by size, with example importer chain:`);
    for (const t of transitive.sort((a, b) => b.gz - a.gz).slice(0, 15)) {
        console.log(`  ${fmtKB(t.gz)}  ${t.base}`);
        console.log(`           via ${t.chain.join(" → ")}`);
    }
    if (transitive.length > 15) {
        console.log(`  ... ${transitive.length - 15} more transitive chunks`);
    }
    console.log("");
}

if (direct.length) {
    console.log(`DIRECT DEPS — top direct deps by size:`);
    for (const d of direct.sort((a, b) => b.gz - a.gz).slice(0, 15)) {
        console.log(`  ${fmtKB(d.gz)}  ${d.base}  ${d.directHits.length > 1 ? `(scope: ${d.directHits.join(", ")})` : ""}`);
    }
    console.log("");
}

if (vendor.length) {
    console.log(`VENDOR BUCKETS — manualChunks aggregated all matching packages here:`);
    for (const v of vendor.sort((a, b) => b.gz - a.gz)) {
        console.log(`  ${fmtKB(v.gz)}  ${v.base}`);
    }
    console.log("");
}

if (declaredButUnused.length) {
    console.log(`DECLARED BUT NOT IMPORTED — in ${ui}/package.json but no src/ file imports them:`);
    console.log(`  (some are dev-only and harmless; verify before removing)`);
    for (const d of declaredButUnused) console.log(`  - ${d}`);
    console.log("");
}

if (importedButUndeclared.length) {
    console.log(`IMPORTED BUT NOT DECLARED — relied on via hoisting from a sibling workspace:`);
    for (const d of importedButUndeclared) console.log(`  - ${d}`);
    console.log("");
}

// Exit non-zero if there are orphans (= probable bug).
process.exit(orphan.length > 0 ? 1 : 0);
