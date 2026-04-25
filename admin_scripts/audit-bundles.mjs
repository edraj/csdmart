// Scans built JS chunks for module-top-level bare identifier references
// that aren't a known browser/Node global. These are the pattern that
// blew up /cxb in v0.8.16 (Prism is not defined).
//
// Usage: node admin_scripts/audit-bundles.mjs <dist/client/assets dir>

import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { Parser } from "acorn";
import * as walk from "acorn-walk";

// Browser + Node + ECMAScript host globals. Anything outside this list
// referenced at module top level without a declaration is a candidate
// landmine. Conservatively wide so false-positive noise stays low.
const KNOWN_GLOBALS = new Set([
    // ECMAScript
    "globalThis", "Object", "Array", "Function", "Boolean", "Number", "String",
    "Symbol", "BigInt", "Math", "Date", "JSON", "RegExp", "Error", "TypeError",
    "RangeError", "ReferenceError", "SyntaxError", "EvalError", "URIError",
    "AggregateError", "Promise", "Proxy", "Reflect", "Map", "Set", "WeakMap",
    "WeakSet", "WeakRef", "FinalizationRegistry", "ArrayBuffer", "SharedArrayBuffer",
    "DataView", "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array",
    "Uint16Array", "Int32Array", "Uint32Array", "Float32Array", "Float64Array",
    "BigInt64Array", "BigUint64Array", "Atomics", "Intl", "Iterator",
    "AsyncIterator", "Generator", "AsyncGenerator", "GeneratorFunction",
    "AsyncGeneratorFunction", "AsyncFunction",
    "undefined", "NaN", "Infinity",
    "isNaN", "isFinite", "parseInt", "parseFloat", "encodeURI", "decodeURI",
    "encodeURIComponent", "decodeURIComponent", "escape", "unescape", "eval",
    // Browser
    "window", "document", "navigator", "location", "history", "screen",
    "localStorage", "sessionStorage", "indexedDB", "performance", "console",
    "alert", "confirm", "prompt", "fetch", "XMLHttpRequest", "WebSocket",
    "EventSource", "FormData", "Headers", "Request", "Response", "URL",
    "URLSearchParams", "Blob", "File", "FileReader", "FileList", "ImageData",
    "ImageBitmap", "OffscreenCanvas", "Worker", "SharedWorker", "ServiceWorker",
    "Notification", "PushManager", "Cache", "CacheStorage", "Crypto", "crypto",
    "SubtleCrypto", "TextEncoder", "TextDecoder", "structuredClone",
    "queueMicrotask", "reportError", "atob", "btoa",
    "setTimeout", "clearTimeout", "setInterval", "clearInterval",
    "requestAnimationFrame", "cancelAnimationFrame", "requestIdleCallback",
    "cancelIdleCallback", "MutationObserver", "ResizeObserver",
    "IntersectionObserver", "PerformanceObserver", "BroadcastChannel",
    "MessageChannel", "MessageEvent", "Event", "EventTarget", "CustomEvent",
    "ErrorEvent", "PromiseRejectionEvent", "AbortController", "AbortSignal",
    "DOMException", "HTMLElement", "Element", "Node", "Document", "Text",
    "Comment", "DocumentFragment", "ShadowRoot", "Range", "Selection",
    "WeakSet", "ReadableStream", "WritableStream", "TransformStream",
    "ByteLengthQueuingStrategy", "CountQueuingStrategy", "WebAssembly",
    "matchMedia", "getComputedStyle", "getSelection", "open", "close",
    "scrollTo", "scrollBy", "scrollX", "scrollY", "innerWidth", "innerHeight",
    "outerWidth", "outerHeight", "devicePixelRatio", "screenX", "screenY",
    "self", "top", "parent", "frames", "origin", "name",
    "FontFace", "FontFaceSet", "DOMParser", "XMLSerializer", "XPathResult",
    "NodeFilter", "NodeIterator", "TreeWalker", "Path2D", "DOMRect",
    "DOMRectReadOnly", "DOMPoint", "DOMPointReadOnly", "DOMMatrix",
    "DOMMatrixReadOnly", "DOMQuad", "Image", "Audio", "Video",
    "HTMLCanvasElement", "HTMLImageElement", "HTMLVideoElement", "HTMLAudioElement",
    "HTMLInputElement", "HTMLFormElement", "HTMLSelectElement", "HTMLTextAreaElement",
    "HTMLButtonElement", "HTMLAnchorElement", "HTMLDivElement", "HTMLSpanElement",
    "HTMLScriptElement", "HTMLStyleElement", "HTMLLinkElement", "HTMLTemplateElement",
    "CSSStyleDeclaration", "CSSStyleSheet", "CSSRule", "MediaQueryList",
    "Storage", "PerformanceEntry", "PerformanceMark", "PerformanceMeasure",
    "PerformanceNavigation", "PerformanceTiming", "PerformanceResourceTiming",
    "WebGLRenderingContext", "WebGL2RenderingContext", "CanvasRenderingContext2D",
    "CanvasGradient", "CanvasPattern", "TouchEvent", "PointerEvent",
    "MouseEvent", "KeyboardEvent", "WheelEvent", "FocusEvent", "InputEvent",
    "DragEvent", "ClipboardEvent", "AnimationEvent", "TransitionEvent",
    "GamepadEvent", "Gamepad", "Geolocation", "MediaDevices", "MediaStream",
    "MediaStreamTrack", "RTCPeerConnection", "RTCDataChannel", "MediaRecorder",
    "AudioContext", "AudioWorklet", "AudioWorkletProcessor", "AudioBuffer",
    "AnalyserNode", "GainNode", "OscillatorNode",
    "trustedTypes", "TrustedHTML", "TrustedScript", "TrustedScriptURL",
    // Node-ish (rare in browser bundles but harmless to allow)
    "process", "Buffer", "global", "require", "module", "exports", "__dirname",
    "__filename", "import", "queueMicrotask",
    // Worker context (some chunks are universal)
    "WorkerGlobalScope", "DedicatedWorkerGlobalScope", "SharedWorkerGlobalScope",
    "ServiceWorkerGlobalScope", "importScripts", "postMessage", "onmessage",
    "onerror", "onunhandledrejection", "onrejectionhandled",
    // Vite/rolldown-injected
    "__vite__mapDeps", "__vite_import_meta_env__",
]);

const SOURCE_TYPE_FALLBACKS = ["module", "script"];

function parse(code, filename) {
    let lastErr;
    for (const sourceType of SOURCE_TYPE_FALLBACKS) {
        try {
            return Parser.parse(code, {
                ecmaVersion: "latest",
                sourceType,
                allowReturnOutsideFunction: true,
                allowAwaitOutsideFunction: true,
                allowImportExportEverywhere: true,
                allowHashBang: true,
                locations: true,
            });
        } catch (e) {
            lastErr = e;
        }
    }
    throw new Error(`parse ${filename}: ${lastErr.message}`);
}

// Collect names introduced by a Pattern node (function param, var binding).
function bindingsFromPattern(node, out) {
    if (!node) return;
    switch (node.type) {
        case "Identifier":
            out.add(node.name); break;
        case "ObjectPattern":
            for (const p of node.properties) {
                if (p.type === "Property") bindingsFromPattern(p.value, out);
                else if (p.type === "RestElement") bindingsFromPattern(p.argument, out);
            }
            break;
        case "ArrayPattern":
            for (const el of node.elements) bindingsFromPattern(el, out);
            break;
        case "RestElement":
            bindingsFromPattern(node.argument, out); break;
        case "AssignmentPattern":
            bindingsFromPattern(node.left, out); break;
    }
}

// Collect every binding visible at the module top level. `var`
// declarations hoist out of nested blocks (for-loop init, if/else,
// switch cases, try/catch bodies); `let`/`const` are block-scoped and
// only count when at the program body level. We never recurse into
// function/class bodies — their inner bindings aren't visible to the
// module top level.
function collectTopLevelBindings(program) {
    const declared = new Set();
    walk.recursive(program, null, {
        Function() { /* skip — different scope */ },
        Class() { /* skip */ },
        VariableDeclaration(node, _state, c) {
            for (const d of node.declarations) {
                bindingsFromPattern(d.id, declared);
                if (d.init) c(d.init, _state);
            }
        },
        FunctionDeclaration(node) { if (node.id) declared.add(node.id.name); },
        ClassDeclaration(node) { if (node.id) declared.add(node.id.name); },
        ImportDeclaration(node) {
            for (const spec of node.specifiers) declared.add(spec.local.name);
        },
        ExportNamedDeclaration(node, _state, c) {
            if (node.declaration) c(node.declaration, _state);
        },
        ExportDefaultDeclaration(node) {
            if (node.declaration && node.declaration.id) declared.add(node.declaration.id.name);
        },
        // `catch (err) { ... }` introduces `err` block-scoped; we don't
        // need it for module-level analysis but harmless to include.
        CatchClause(node, _state, c) {
            if (node.param) bindingsFromPattern(node.param, declared);
            c(node.body, _state);
        },
    });
    return declared;
}

// Walk every Identifier reference inside a top-level statement, treating
// function/arrow/class scopes as opaque (their inner refs resolve at call
// time, not at module load — out of scope for this audit). The walk does
// NOT descend into FunctionDeclaration/Expression/ArrowFunction bodies.
function collectTopLevelReferences(stmt, refs) {
    walk.recursive(stmt, null, {
        FunctionDeclaration() { /* skip body */ },
        FunctionExpression() { /* skip body */ },
        ArrowFunctionExpression() { /* skip body */ },
        ClassDeclaration() { /* skip body */ },
        ClassExpression() { /* skip body */ },
        Property(node, _state, c) {
            // Don't visit shorthand keys or computed-property keys as refs
            // unless they'd actually evaluate at runtime. A non-computed,
            // non-shorthand key like `{foo: bar}` only treats `bar` as ref.
            if (node.computed) c(node.key, _state);
            c(node.value, _state);
        },
        MemberExpression(node, _state, c) {
            c(node.object, _state);
            if (node.computed) c(node.property, _state);
        },
        // `typeof X` is safe even when X is undeclared — never throws.
        UnaryExpression(node, _state, c) {
            if (node.operator === "typeof" && node.argument.type === "Identifier") return;
            c(node.argument, _state);
        },
        Identifier(node) {
            refs.push({ name: node.name, line: node.loc.start.line, col: node.loc.start.column });
        },
        // Ignore label names and similar non-reference identifiers.
        LabeledStatement(node, _state, c) { c(node.body, _state); },
        BreakStatement() {},
        ContinueStatement() {},
    });
}

function auditFile(filename) {
    const code = readFileSync(filename, "utf8");
    let ast;
    try {
        ast = parse(code, filename);
    } catch (e) {
        return [{ name: "<parse-error>", line: 0, col: 0, message: e.message }];
    }
    const declared = collectTopLevelBindings(ast);
    const findings = [];
    for (const stmt of ast.body) {
        // Skip declarations themselves — only walk *values* of top-level
        // statements where references actually fire at module load.
        const refs = [];
        if (stmt.type === "ImportDeclaration") continue;
        if (stmt.type === "FunctionDeclaration") continue;
        if (stmt.type === "ClassDeclaration") continue;
        collectTopLevelReferences(stmt, refs);
        for (const ref of refs) {
            if (declared.has(ref.name)) continue;
            if (KNOWN_GLOBALS.has(ref.name)) continue;
            findings.push(ref);
        }
    }
    return findings;
}

const dir = process.argv[2];
if (!dir) {
    console.error("usage: audit-bundles.mjs <assets-dir>");
    process.exit(2);
}

const files = readdirSync(dir).filter(f => f.endsWith(".js")).sort();
let total = 0;
const grouped = new Map();
for (const f of files) {
    const findings = auditFile(join(dir, f));
    for (const finding of findings) {
        total++;
        const key = finding.name;
        if (!grouped.has(key)) grouped.set(key, []);
        grouped.get(key).push({ file: f, line: finding.line, col: finding.col, message: finding.message });
    }
}

if (total === 0) {
    console.log("clean — no top-level undeclared references in any chunk");
    process.exit(0);
}

console.log(`${total} top-level undeclared reference(s) across ${grouped.size} identifier(s):\n`);
for (const [name, hits] of [...grouped.entries()].sort((a, b) => b[1].length - a[1].length)) {
    console.log(`  ${name}  (${hits.length} hit${hits.length > 1 ? "s" : ""})`);
    for (const hit of hits.slice(0, 5)) {
        const where = hit.message ? ` ${hit.message}` : ` line ${hit.line}:${hit.col}`;
        console.log(`    ${hit.file}${where}`);
    }
    if (hits.length > 5) console.log(`    ... and ${hits.length - 5} more`);
}
process.exit(1);
