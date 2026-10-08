/**
 * Small JSON-Schema helpers shared by anything that needs to read a dmart
 * schema's `payload.body` (the SchemaViewer and the admin folder filter
 * panel). Schemas here commonly do two things plain `properties` lookups
 * don't handle: wrap shared fields as `{ allOf: [{ $ref: "#/definitions/x" }] }`,
 * and define discriminated unions as a root-level `oneOf`/`anyOf` with no
 * top-level `properties` at all (each branch has its own).
 */
import { isIndexable, isJsonObject, type JsonSchemaNode } from "./types";

/**
 * A schema body read as a schema node, or null for anything that is not an
 * object. The one place the stored document is given its declared shape.
 */
export function asSchemaNode(value: unknown): JsonSchemaNode | null {
  return isJsonObject(value) ? (value as JsonSchemaNode) : null;
}

/** Resolves a "#/definitions/x"-style pointer against the root schema document. */
export function resolveSchemaRef(root: JsonSchemaNode | null | undefined, ref: string): JsonSchemaNode | null {
  if (!ref || typeof ref !== "string" || !ref.startsWith("#/")) return null;
  const path = ref.slice(2).split("/");
  let cur: unknown = root;
  for (const segment of path) {
    if (!isIndexable(cur)) return null;
    cur = cur[segment];
  }
  return asSchemaNode(cur);
}

/** Flattens `$ref`/`allOf` indirection so callers can read type/title/enum directly. */
export function resolveSchemaDef(root: JsonSchemaNode | null | undefined, def: unknown): JsonSchemaNode | null {
  const node = asSchemaNode(def);
  if (!node) return null;
  if (!node.allOf && !node.$ref) return node;

  const sources: JsonSchemaNode[] = [];
  if (typeof node.$ref === "string") {
    const resolved = resolveSchemaRef(root, node.$ref);
    const flattened = resolved && resolveSchemaDef(root, resolved);
    if (flattened) sources.push(flattened);
  }
  if (Array.isArray(node.allOf)) {
    for (const part of node.allOf) {
      const flattened = resolveSchemaDef(root, part);
      if (flattened) sources.push(flattened);
    }
  }

  const own: JsonSchemaNode = { ...node };
  delete own.allOf;
  delete own.$ref;
  const merged: JsonSchemaNode = {};
  for (const source of sources) Object.assign(merged, source);
  return Object.assign(merged, own);
}

/**
 * Returns every `properties` bag defined on a schema body — one bag for a
 * plain schema, or one per `oneOf`/`anyOf` branch for a discriminated union
 * that has no top-level `properties`.
 */
export function collectSchemaPropertyBags(body: unknown): Array<Record<string, JsonSchemaNode>> {
  const node = asSchemaNode(body);
  if (!node) return [];
  if (node.properties && typeof node.properties === "object") {
    return [node.properties];
  }
  const branches = Array.isArray(node.oneOf)
    ? node.oneOf
    : Array.isArray(node.anyOf)
      ? node.anyOf
      : undefined;
  if (!branches) return [];
  return branches
    .map((branch) => asSchemaNode(branch)?.properties)
    .filter((p): p is Record<string, JsonSchemaNode> => !!p && typeof p === "object");
}
