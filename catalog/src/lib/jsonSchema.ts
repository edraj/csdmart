/**
 * Small JSON-Schema helpers shared by anything that needs to read a dmart
 * schema's `payload.body` (the SchemaViewer and the admin folder filter
 * panel). Schemas here commonly do two things plain `properties` lookups
 * don't handle: wrap shared fields as `{ allOf: [{ $ref: "#/definitions/x" }] }`,
 * and define discriminated unions as a root-level `oneOf`/`anyOf` with no
 * top-level `properties` at all (each branch has its own).
 */

/** Resolves a "#/definitions/x"-style pointer against the root schema document. */
export function resolveSchemaRef(root: any, ref: string): any {
  if (!ref || typeof ref !== "string" || !ref.startsWith("#/")) return null;
  const path = ref.slice(2).split("/");
  let cur = root;
  for (const segment of path) {
    if (cur == null) return null;
    cur = cur[segment];
  }
  return cur ?? null;
}

/** Flattens `$ref`/`allOf` indirection so callers can read type/title/enum directly. */
export function resolveSchemaDef(root: any, def: any): any {
  if (!def || typeof def !== "object") return def;
  if (!def.allOf && !def.$ref) return def;

  const sources: any[] = [];
  if (typeof def.$ref === "string") {
    const resolved = resolveSchemaRef(root, def.$ref);
    if (resolved) sources.push(resolveSchemaDef(root, resolved));
  }
  if (Array.isArray(def.allOf)) {
    for (const part of def.allOf) {
      if (part && typeof part === "object") {
        sources.push(resolveSchemaDef(root, part));
      }
    }
  }

  const own = { ...def };
  delete own.allOf;
  delete own.$ref;
  return Object.assign({}, ...sources, own);
}

/**
 * Returns every `properties` bag defined on a schema body — one bag for a
 * plain schema, or one per `oneOf`/`anyOf` branch for a discriminated union
 * that has no top-level `properties`.
 */
export function collectSchemaPropertyBags(body: any): Array<Record<string, any>> {
  if (!body || typeof body !== "object") return [];
  if (body.properties && typeof body.properties === "object") {
    return [body.properties];
  }
  const branches: any[] | undefined = Array.isArray(body.oneOf)
    ? body.oneOf
    : Array.isArray(body.anyOf)
      ? body.anyOf
      : undefined;
  if (!branches) return [];
  return branches
    .map((b) => b?.properties)
    .filter((p): p is Record<string, any> => !!p && typeof p === "object");
}
