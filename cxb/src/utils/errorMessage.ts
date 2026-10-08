/**
 * One place that turns whatever a failed call threw into text a person can
 * read. Axios errors carry the server's envelope under `response.data`, dmart
 * puts its message at `error.message` inside that envelope, plain Errors have
 * `.message`, and some callers throw strings or raw objects. Nothing here can
 * itself throw, and the result is never "[object Object]".
 */
export function errorMessage(error: unknown, fallback = ""): string {
    if (error === null || error === undefined) return fallback;
    if (typeof error === "string") return error || fallback;
    if (typeof error !== "object") return String(error);

    const e = error as {
        response?: { data?: unknown; status?: number; statusText?: string };
        message?: unknown;
    };

    const data = e.response?.data;
    if (data !== undefined && data !== null) {
        if (typeof data === "string" && data.trim()) return data;
        if (typeof data === "object") {
            const d = data as { error?: unknown; message?: unknown };
            const err = d.error;
            if (typeof err === "string" && err.trim()) return err;
            if (err && typeof err === "object") {
                const inner = (err as { message?: unknown; info?: unknown }).message;
                if (typeof inner === "string" && inner.trim()) return inner;
            }
            if (typeof d.message === "string" && d.message.trim()) return d.message;
        }
    }

    if (typeof e.message === "string" && e.message.trim()) return e.message;

    if (e.response?.status) {
        return `${e.response.status} ${e.response.statusText ?? ""}`.trim();
    }

    try {
        const json = JSON.stringify(error);
        return json && json !== "{}" ? json : fallback;
    } catch {
        return fallback;
    }
}
