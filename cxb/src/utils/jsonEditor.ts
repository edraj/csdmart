/**
 * What the JSON editor holds: svelte-jsoneditor's `Content` is either
 * `{ json }` or `{ text }`, but callers also hand this an already-parsed
 * entry (the renderer's original snapshot) and expect it back untouched.
 *
 * `T` names the shape the caller knows it put into the editor; the parser
 * itself can only promise an object.
 */
export function jsonEditorContentParser<T extends Record<string, unknown> = Record<string, unknown>>(
    jeContent: unknown,
): T {
    if (jeContent === undefined || jeContent === null) {
        return {} as T;
    }
    const content = jeContent as { json?: unknown; text?: unknown };
    if (content.json) {
        return structuredClone(content.json) as T;
    } else if (content.text) {
        try {
            return JSON.parse(String(content.text)) as T;
        } catch {
            throw new Error("Invalid JSON content in editor");
        }
    }
    return jeContent as T;
}
