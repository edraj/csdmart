// Converts a JSON schema between its stored shape and the shape the schema
// editor form edits.
//
// Stored:   { type: "object", properties: { name: { type: "string" } } }
// Form:     { id, type: "object", properties: [ { id, name: "name", type: "string", title: "", description: "" } ] }
//
// The form needs `properties` as an ordered array (so rows can be reordered
// and each has a stable `id` for keyed rendering), and the stored schema needs
// it back as an object keyed by property name. Both directions are recursive.
//
// This is catalog's version, which cxb's had fallen behind: it keeps a removed
// property as `{ name, __removed: true }` in the form and writes it back as
// `null`, so a PATCH can delete a property instead of silently keeping it, and
// it skips null rows rather than crashing on them.
import { generateUUID } from "./uuid";

// A schema node as the editor sees it: an open record. The editor reads and
// writes arbitrary JSON-schema keywords, so the shape is deliberately loose.
export type SchemaNode = Record<string, unknown>;

type FormProperty = SchemaNode & { name?: string; __removed?: boolean };

function isRecord(value: unknown): value is SchemaNode {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

// Form -> stored JSON. Mutates and returns `obj`.
export function transformFormToJson(obj: unknown): unknown {
  if (obj === null) return null;
  if (typeof obj !== "object") return obj;

  if (Array.isArray(obj)) {
    return obj.filter((item) => item !== null).map(transformFormToJson);
  }

  const node = obj as SchemaNode;
  delete node.id;

  for (const key of Object.keys(node)) {
    if (key !== "id") {
      node[key] = transformFormToJson(node[key]);
    }
    if (key === "properties") {
      node.properties = convertArrayToObject(node.properties);
    }
  }

  // An array's `items` is edited as if it were an object with properties;
  // collapse a single property back to a bare item type.
  if (node.type === "array" && isRecord(node.items)) {
    const items = node.items;
    const itemProperties = isRecord(items.properties) ? items.properties : {};
    const keys = Object.keys(itemProperties);
    if (keys.length === 1) {
      const only = itemProperties[keys[0]];
      items.type = isRecord(only) ? only.type : undefined;
    } else if (keys.length > 1) {
      items.type = "object";
      if (items.additionalProperties === undefined) {
        items.additionalProperties = false;
      }
    }
  }

  return node;
}

// `[ { name: "a", ... }, { name: "b", __removed: true } ]` ->
// `{ a: { ... }, b: null }`. Non-arrays pass through untouched.
export function convertArrayToObject(arr: unknown): unknown {
  if (!Array.isArray(arr)) return arr;
  const obj: SchemaNode = {};
  for (const raw of arr as FormProperty[]) {
    if (raw === null) continue;
    if (raw.__removed && raw.name) {
      obj[raw.name] = null;
      continue;
    }
    const key = raw.name;
    if (key === undefined) continue;
    delete raw.name;
    obj[key] = raw;
  }
  return obj;
}

// Stored JSON -> form. Returns a copy; every node gets an `id`.
export function transformJsonToForm(obj: unknown): unknown {
  if (!obj || typeof obj !== "object") return obj;

  if (Array.isArray(obj)) {
    return obj.filter((item) => item !== null).map(transformJsonToForm);
  }

  const result: SchemaNode = { ...(obj as SchemaNode) };
  if (result.id === undefined) {
    result.id = generateUUID();
  }

  for (const key of Object.keys(result)) {
    if (key !== "id") {
      result[key] = transformJsonToForm(result[key]);
    }
    if (key === "properties") {
      result.properties = convertObjectToArray(result.properties);
    }
  }

  return result;
}

// `{ a: { ... }, b: null }` -> `[ { name: "a", title: "", description: "", ... }, { name: "b", __removed: true } ]`.
// Non-objects pass through untouched.
export function convertObjectToArray(obj: unknown): unknown {
  if (!isRecord(obj)) return obj;
  const arr: FormProperty[] = [];
  for (const key of Object.keys(obj)) {
    if (key === "id") continue;
    const value = obj[key];
    if (value === null) {
      arr.push({ name: key, __removed: true });
      continue;
    }
    const item: FormProperty = { name: key, ...(isRecord(value) ? value : {}) };
    if (item.title === undefined) item.title = "";
    if (item.description === undefined) item.description = "";
    arr.push(item);
  }
  return arr;
}
