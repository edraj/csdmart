# CXB Frontend — Code Audit & Improvements

**Date:** 2026-04-02
**Scope:** 24 files across `cxb/src/`, config, and build files

---

## 1. Critical Bugs Fixed

### 1.1 Operator Precedence Bug — `entryManagement.ts:51`

**Problem:** Due to `&&` binding tighter than `||`, the password-deletion logic applied to
**all** resource types instead of only `ResourceType.user`:

```typescript
// BEFORE — broken precedence
if (resource_type === ResourceType.user && content.password === null || (...))
// Evaluated as: (user && null) || (...)  — the || branch runs for ALL types
```

**Fix:** Added explicit parentheses so the entire `||` group is gated by the user check:

```typescript
// AFTER
if (resource_type === ResourceType.user && (content.password === null || (...)))
```

### 1.2 `$goto` in Plain `.ts` File — `metaFormUtils.ts:3,177`

**Problem:** A bare `$goto` statement at line 3 (module-level side-effect expression) and
`$goto(navInfo.url, navInfo.payload)` at line 177. `$goto` is a Routify reactive store that
only works inside `.svelte` files. In a `.ts` file this is a `ReferenceError` at runtime.

**Fix:** Removed bare `$goto`. Changed `updateEntryShortname()` to return navigation info
(`{ url, payload }`) so the calling `.svelte` component can handle routing itself.

### 1.3 `removeEmpty` Was a No-Op — `schemaEntryRenderer.ts:27-65`

**Problem:** The entire function body for array/object/string cleaning was commented out.
The function only checked for null/undefined and returned data as-is. Since this function
is actively called by `entryManagement.ts` before saving entries, data was never cleaned.

**Fix:** Restored the full recursive implementation that handles arrays, nested objects,
and empty strings. Also replaced the custom UUID import with `crypto.randomUUID()`.

### 1.4 `hideFolders.length` on `undefined` — `ListView.svelte:191`

**Problem:** `currentSpace?.attributes?.hide_folders` can be `undefined` (optional chaining
returns `undefined`), but line 191 called `.length` on it without a null guard, causing a
`TypeError` crash when a space has no `hide_folders` attribute.

**Fix:** Changed to `hideFolders?.length` with optional chaining.

---

## 2. Error Handling Fixed

### 2.1 `fetchWorkflows` Missing Return — `dmart_services.ts:113`

**Problem:** The catch block showed a toast but returned `undefined` (implicit). Callers
expecting an array would crash when trying to iterate.

**Fix:** Added `return []` in the catch block.

### 2.2 Unsafe `JSON.parse(localStorage)` — `stores/user.ts:40`

**Problem:** `JSON.parse(data)` where `data` comes from `localStorage.getItem(KEY)` could
contain corrupted data, causing an unhandled exception during app initialization.

**Fix:** Wrapped in try/catch with fallback to signedout state.

### 2.3 Unsafe `JSON.parse` in i18n — `i18n/index.ts:27-31`

**Problem:** `JSON.parse(preferred_locale)` was guarded by a `typeof` check but had no
try/catch around the parse itself.

**Fix:** Wrapped in try/catch; falls back to browser locale detection on parse failure.

### 2.4 Unprotected `JSON.parse` for Roles — `MetaTicketForm.svelte:22`

**Problem:** `JSON.parse(localStorage.getItem("roles"))` with no try/catch. If localStorage
contains invalid JSON, this crashes the entire component.

**Fix:** Wrapped in try/catch with empty array fallback.

---

## 3. Build & Configuration Fixed

### 3.1 `tsconfig.json` — Removed `vitest/globals`, Added `strict`

**Problem:** Referenced `vitest/globals` types (no tests exist in the project), causing a
TypeScript error. No `strict` mode enabled, so implicit-any and null-safety bugs were
invisible.

**Fix:** Removed `vitest/globals`, added `"strict": true` and `"noImplicitAny": false`
(incremental strictness — enables null checks without requiring immediate any-elimination).

### 3.2 `vite-env.d.ts` — Uncommented Type References

**Problem:** All four `/// <reference types="..." />` directives were commented out,
meaning Vite/Svelte global types were not loaded.

**Fix:** Uncommented `svelte` and `vite/client` references.

### 3.3 `.env.example:9` — Invalid Syntax

**Problem:** Used `:` (YAML-like syntax) instead of `=` for `VITE_WEBSOCKET`. This is
invalid `.env` syntax and would not be parsed correctly by dotenv.

**Fix:** Changed to `=`. Also changed `0.0.0.0` to `localhost`.

### 3.4 `config.ts` — Fallback Websocket Bound to All Interfaces

**Problem:** Default/fallback websocket URL was `ws://0.0.0.0:8484/ws`, which binds to all
network interfaces — a security concern in production.

**Fix:** Changed to `ws://localhost:8484/ws`.

---

## 4. Code Quality Improvements

### 4.1 `var` → `let` — `App.svelte:44`

Changed `var appRouter = null` to `let appRouter = null` to avoid function-scoped hoisting.

### 4.2 Hardcoded Prefix — `App.svelte:7`

**Before:** `const prefix='cxb'` — hardcoded, ignoring the Vite base URL config.

**After:** `const prefix = (import.meta.env.BASE_URL || '/cxb').replace(/^\/|\/$/g, '')` —
derives from the build configuration.

### 4.3 Hardcoded Locale on Signin — `stores/user.ts:55`

**Before:** `locale: Locale.ar` — forced Arabic on every signin regardless of user preference.

**After:** `locale: guess_locale()` — uses the browser locale detection function that
already existed in the file.

### 4.4 `Object` Type — `stores/user.ts:16`

Changed `account?: Object` to `account?: Record<string, unknown>` for proper typing.

### 4.5 Loose Equality (`==` / `!=`) → Strict (`===` / `!==`)

Fixed across 6 files:

| File | Line(s) |
|---|---|
| `stores/user.ts` | 45 |
| `dmart_services.ts` | 75 |
| `toast.ts` | 14 |
| `listViewUtils.ts` | 18, 19 |
| `compare.ts` | 52 |
| `getFileExtension.ts` | 3 |

### 4.6 Default Parameter Fix — `toast.ts:10`

Changed `message: string = undefined` to `message?: string` (proper optional parameter).

### 4.7 `let` → `const` — `getFileExtension.ts:2`

Changed `let ext =` to `const ext =` since the variable is never reassigned.

---

## 5. Type Safety Improvements

### 5.1 `attachments.ts` — Full Type Annotations + Rename

- Added `CSVParseResult` interface for the return type of `parseCSV`.
- Added type annotations to all function parameters and return types.
- Renamed `pyTOjs` to `pyToJs` (consistent camelCase naming).

### 5.2 `helpers.ts` — Interface + Lookup Maps

- Added `StatefulEntity` interface to replace `any` parameter.
- Replaced repeated if-chains in `renderStateString`/`renderStateIcon` with
  `STATE_LABELS` and `STATE_ICONS` lookup maps.
- Added `maxLength` parameter to `truncateString` for flexibility.

### 5.3 `jsonEditor.ts` — Return Type

Added explicit `Record<string, any>` return type to `jsonEditorContentParser`.

### 5.4 `compare.ts` — Typed Parameters

Changed `isDeepEqual(x: any, y: any)` to `isDeepEqual(x: unknown, y: unknown): boolean`.
Added `Record<string, any>` type to `removeEmpty` parameter and return.

### 5.5 `checkAccess.ts` — Typed Array

Changed untyped `const oks = []` to `const oks: boolean[] = []`.

### 5.6 `dmart_services.ts` — Missing Parameter Types

- `getChildrenAndSubChildren`: added `string` type for `spacename`, `string[]` for
  `subpathsPTR`, `Promise<void>` return type.

---

## 6. Null Safety (Strict Mode)

Enabling `strict: true` in tsconfig revealed several null-safety issues that were fixed:

| File | Issue | Fix |
|---|---|---|
| `dmart_services.ts:19-21` | `Dmart.query()` can return `null` | Added `!results` guard before accessing `.records` |
| `dmart_services.ts:65-88` | `folders` possibly null | Added null check with descriptive error throw |
| `dmart_services.ts:76-77` | `selectedSpace.attributes.hide_folders` unsafe chain | Added `?.` optional chaining throughout |
| `dmart_services.ts:110` | `result` possibly null | Changed to `result?.records` |
| `listViewUtils.ts:48` | `localStorage.getItem()` returns `string | null` | Refactored to explicit null check before `parseInt` |
| `listViewUtils.ts:178-182` | `Dmart.query()` can return `null` | Added null guard with `{ total: 0, records: [] }` fallback |
| `entryManagement.ts:84` | `removeEmpty` now returns `unknown` | Added explicit cast to `Record<string, any>` |

---

## 7. Performance & Dead Code

### 7.1 O(n^2) Bulk Selection — `ListView.svelte:416-434`

**Problem:** `handleAllBulk` iterated all rows, calling `document.getElementById` for each,
and on each toggle created a new spread array `[...$bulkBucket, {...}]` or a new filtered
array. For large datasets this was O(n^2).

**Fix:** Replaced with a single-pass approach: select-all builds the full list with one
`.map()`, deselect-all assigns an empty array.

### 7.2 Custom UUID Generator — `uuid.ts`

**Problem:** 23-line custom UUID generator using `Math.random()` and timestamp mixing.

**Fix:** Replaced with `crypto.randomUUID()` (native, cryptographically secure, available
in all modern browsers). The function is kept as a deprecated wrapper for existing imports.
`schemaEditorUtils.ts` now calls `crypto.randomUUID()` directly.

### 7.3 Dead Code Removed

- `schemaEntryRenderer.ts:33-62` — 30 lines of commented-out function body (now restored
  as working code).
- `timeago.ts:3` — Commented-out `MONTH_NAMES` array.

---

## 8. Remaining Items (Not Addressed)

These items were identified during the audit but are larger changes that require
coordination or design decisions:

1. **Auth token in localStorage** — JWT stored in `localStorage` is vulnerable to XSS.
   Moving to `httpOnly` cookies requires backend changes.

2. **Client-side permission checks from localStorage** — `checkAccess.ts` and
   `MetaTicketForm.svelte` read permissions/roles from localStorage. Users can modify these
   in DevTools. Server-side enforcement is essential (and may already exist).

3. **Direct DOM manipulation in Svelte components** — `ListView.svelte` uses
   `document.getElementById`, `innerHTML`, and `classList` manipulations in
   `handleSortRendered`. These fight Svelte's reactivity system and should be replaced with
   Svelte bindings/actions, but this requires restructuring the component.

4. **Mutable `let` export for config** — `config.ts` exports `website` as a `let` that is
   reassigned asynchronously. Modules that import `website` at the top level during module
   initialization may get stale defaults (e.g., `i18n/index.ts:13`). Consider using a
   reactive store or ensuring all consumers await `configReady`.

5. **IIFE inside `$effect`** — `ListView.svelte:356-364` uses an async IIFE inside a
   `$effect` block. Unhandled promise rejections from the IIFE are invisible to the effect
   system. This is a Svelte 5 anti-pattern.

6. **Git dependencies** — `package.json` references `cl-editor` and
   `svelte-datatables-net` from GitHub repos without pinned commits/tags, making builds
   non-reproducible.

7. **Missing frontend tests** — No test files exist in the project despite `vitest` being
   referenced in the old tsconfig.

8. **`getAvatar` called on every render** — `ManagementHeader.svelte:148` fires a new API
   request on every render with no caching. The avatar should be fetched once and stored.

9. **Pervasive `any` usage** — ~50+ occurrences remain across TypeScript files. Strict mode
   is now enabled with `noImplicitAny: false` as an incremental step. Setting
   `noImplicitAny: true` and eliminating remaining `any` types is the next logical step.
