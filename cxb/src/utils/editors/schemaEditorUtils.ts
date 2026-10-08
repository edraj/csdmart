// Converts a JSON schema between its stored shape and the shape the schema
// editor form edits.
//
// Stored:   { type: "object", properties: { name: { type: "string" } } }
// Form:     { id, type: "object", properties: [ { id, name: "name", type: "string", title: "", description: "" } ] }
//
// Both directions walk arbitrary JSON, so every step takes and gives
// `unknown`; `SchemaNode` is the open record a step narrows to before it
// reads or writes a keyword. The form-side node type is `SchemaFormNode` in
// `@/utils/schemaFormUtils`.
import { isRecord } from "@/utils/compare";

/** A schema node as the editor sees it: an open record of JSON-schema keywords. */
export type SchemaNode = Record<string, unknown>;

// FORM -> JSON

export function transformFormToJson(obj: unknown): unknown {
    if (obj === null){
        return null;
    }

    if (typeof obj !== "object") {
        return obj;
    }

    if (Array.isArray(obj)) {
        return obj.map(transformFormToJson);
    }

    const node = obj as SchemaNode;
    if (node.id) {
        delete node.id;
    }

    for (const key in node) {
        if (key !== "id") {
            node[key] = transformFormToJson(node[key]);
        }
        if (key === "properties") {
            node.properties = convertArrayToObject(node.properties);
        }
    }

    if (node.type === "array") {
        if (node.items) {
            const itemProperties = isRecord(node.items) && isRecord(node.items.properties)
                ? node.items.properties
                : {};
            if (Object.keys(itemProperties).length === 1) {
                const only = itemProperties[Object.keys(itemProperties)[0]];
                (node.items as SchemaNode).type = isRecord(only) ? only.type : undefined;
            } else {
                if(typeof node.items !== "object"){
                    node.items = {
                        type: "object"
                    }
                }
                const items = node.items as SchemaNode;
                if (items.additionalProperties === undefined) {
                    items.additionalProperties = false;
                }
            }
        }
    }

    return node;
}

// `[ { name: "a", ... }, { name: "b", ... } ]` -> `{ a: { ... }, b: { ... } }`.
// Non-arrays pass through untouched.
export function convertArrayToObject(arr: unknown): unknown {
    if (!Array.isArray(arr)) {
        return arr;
    }
    const obj: SchemaNode = {};

    for (const item of arr as SchemaNode[]) {
        const key = item["name"];
        delete item.name;
        obj[String(key)] = item;
    }
    return obj;
}

// JSON -> FORM
export function transformJsonToForm(obj: unknown): unknown {
    if (!obj || typeof obj !== "object") {
        return obj;
    }
    const node = obj as SchemaNode;
    if (node.id === undefined){
        node.id = crypto.randomUUID();
    }
    if (Array.isArray(obj)) {
        return obj.map(transformJsonToForm);
    }

    const result: SchemaNode = { ...node };

    for (const key in result) {
        if (key !== "id") {
            result[key] = transformJsonToForm(result[key]);
        }
        if (key === "properties") {
            result.properties = convertObjectToArray(result.properties);
        }
    }

    return result;
}

// `{ a: { ... }, b: { ... } }` -> `[ { name: "a", title: "", description: "", ... }, ... ]`.
// Non-objects pass through untouched.
export function convertObjectToArray(obj: unknown): unknown {
    if (!obj || typeof obj !== "object") {
        return obj;
    }

    const arr: SchemaNode[] = [];
    const record = obj as SchemaNode;

    for (const key in record) {
        if (key !== "id" && Object.prototype.hasOwnProperty.call(record, key)) {
            const item: SchemaNode = { name: key, ...(isRecord(record[key]) ? record[key] : {}) };
            if (item.title === undefined){
                item.title = "";
            }
            if (item.description === undefined){
                item.description = "";
            }
            arr.push(item);
        }
    }

    return arr;
}
