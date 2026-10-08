/**
 * Schema form utility functions for manipulating schema structures
 */
import type { JsonSchema } from "@/utils/renderer/rendererUtils";

/**
 * A schema node as the schema editor form edits it. The stored schema keeps
 * `properties` as an object keyed by name; the form keeps them as an ordered
 * array of nodes, each carrying its `name` and a stable `id` for keyed
 * rendering (see `@/utils/editors/schemaEditorUtils` for the conversion).
 * Open-ended like `JsonSchema`: any other keyword is passed through.
 */
export interface SchemaFormNode {
    id?: string;
    name?: string;
    type?: string;
    title?: string;
    description?: string;
    properties?: SchemaFormNode[];
    items?: SchemaFormNode;
    required?: string[];
    // The validation keywords the editor has inputs for.
    minLength?: number;
    maxLength?: number;
    pattern?: string;
    format?: string;
    minimum?: number;
    maximum?: number;
    multipleOf?: number;
    minItems?: number;
    maxItems?: number;
    [keyword: string]: unknown;
}

/** `container[key]` for an object or array container; undefined for anything else. */
function child(container: unknown, key: string): unknown {
    if (container === null || typeof container !== "object") return undefined;
    return (container as Record<string, unknown>)[key];
}

/**
 * Adds a new property to the form content at the specified parent path
 */
export function addProperty(formContent: SchemaFormNode | null | undefined, parentPath = ""): SchemaFormNode {
    // Handle undefined or null formContent
    const content: SchemaFormNode =
        formContent && typeof formContent === "object"
            ? formContent
            : { type: "object", properties: [], required: [] };

    const newProperty: SchemaFormNode = {
        id: crypto.randomUUID(),
        name: "",
        type: "string",
        title: "",
        description: ""
    };

    if (parentPath) {
        const parent = getPropertyByPath(content, parentPath);
        if (parent && !parent.properties) {
            parent.properties = [];
        }
        if (parent) {
            parent.properties!.push(newProperty);
        }
    } else {
        if (!content.properties) {
            content.properties = [];
        }
        content.properties.push(newProperty);
    }

    return { ...content };
}

/**
 * Adds an array item to the specified parent path
 */
export function addArrayItem(formContent: SchemaFormNode, parentPath: string): SchemaFormNode {
    const parent = getPropertyByPath(formContent, parentPath);
    if (parent) {
        if (!parent.items) {
            parent.items = {
                id: crypto.randomUUID(),
                type: "string"
            };
        }

        if (parent.items.type === "object" && !parent.items.properties) {
            parent.items.properties = [];
        }
    }

    return { ...formContent };
}

/**
 * Removes a property at the specified path and index
 */
export function removeProperty(formContent: SchemaFormNode, path: string, index: number): SchemaFormNode {
    const parts = path.split('.');
    let current: unknown = formContent;

    for (let i = 0; i < parts.length - 1; i++) {
        const next = child(current, parts[i]);
        if (!next) return formContent;
        current = next;
    }

    const lastPart = parts[parts.length - 1];
    const target = child(current, lastPart);
    if (!target) return formContent;

    if (Array.isArray(target)) {
        target.splice(index, 1);
    }
    return { ...formContent };
}

/**
 * Gets a property by its path in the form content
 */
export function getPropertyByPath(obj: SchemaFormNode, path: string): SchemaFormNode | null {
    const parts = path.split('.');
    let current: unknown = obj;

    for (const part of parts) {
        const next = child(current, part);
        if (!next) return null;
        current = next;
    }

    // The path was built from the form's own tree, so what it reaches is a node.
    return current as SchemaFormNode;
}

/**
 * Toggles the required status of a property
 */
export function toggleRequired(formContent: SchemaFormNode, propertyName: string): SchemaFormNode {
    if (!formContent.required) {
        formContent.required = [];
    }

    const index = formContent.required.indexOf(propertyName);
    if (index > -1) {
        formContent.required.splice(index, 1);
    } else {
        formContent.required.push(propertyName);
    }

    return { ...formContent };
}

/**
 * Available schema types for form fields
 */
export const schemaTypes = [
    { value: "string", name: "String" },
    { value: "number", name: "Number" },
    { value: "integer", name: "Integer" },
    { value: "boolean", name: "Boolean" },
    { value: "object", name: "Object" },
    { value: "array", name: "Array" },
    { value: "null", name: "Null" }
];

/**
 * Creates a default schema content structure (the stored shape, not the form's)
 */
export function createDefaultSchemaContent(): JsonSchema {
    return {
        type: "object",
        properties: {},
        required: []
    };
}
