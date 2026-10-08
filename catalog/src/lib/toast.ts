// The one toast helper, wrapping @zerodevx/svelte-toast.
//
//   toasts.success($_("ui.link_copied"));
//   toasts.error(message, { sticky: true });   // stays until dismissed
//
// Colours come from the design tokens so toasts follow light/dark. The
// older successToastMessage/errorToastMessage/... exports in
// lib/toasts_messages.ts delegate here, so existing call sites are unchanged.

import { toast } from "@zerodevx/svelte-toast";

export type ToastKind = "success" | "error" | "info" | "warning";

export interface ToastOptions {
  /** Keep the toast until the user dismisses it. */
  sticky?: boolean;
  /** Milliseconds before auto-dismiss (default: the library's 4000). */
  duration?: number;
}

const BACKGROUND: Record<ToastKind, string> = {
  success: "var(--color-success)",
  error: "var(--color-danger)",
  info: "var(--color-info)",
  warning: "var(--color-warning)",
};

/** Push a toast; returns the id so a caller can `toast.pop(id)` early. */
export function notify(kind: ToastKind, message: string, options: ToastOptions = {}): number {
  return toast.push(message, {
    theme: {
      "--toastBackground": BACKGROUND[kind],
      "--toastColor": "var(--color-text-on-primary)",
      "--toastBarBackground": "rgba(255, 255, 255, 0.55)",
    },
    ...(options.sticky ? { initial: 0 } : {}),
    ...(options.duration ? { duration: options.duration } : {}),
  });
}

export const toasts = {
  success: (message: string, options?: ToastOptions) => notify("success", message, options),
  error: (message: string, options?: ToastOptions) => notify("error", message, options),
  info: (message: string, options?: ToastOptions) => notify("info", message, options),
  warning: (message: string, options?: ToastOptions) => notify("warning", message, options),
  /** Dismiss one toast by id, or all when called without one. */
  dismiss: (id?: number) => (id === undefined ? toast.pop(0) : toast.pop(id)),
};

/**
 * Copy text to the clipboard and report the outcome as a toast. Resolves to
 * whether the copy succeeded; never throws (the clipboard API rejects when
 * the page is not focused or the permission is denied).
 */
export async function copyToClipboard(
  text: string,
  labels: { copied: string; failed: string },
): Promise<boolean> {
  try {
    if (typeof navigator === "undefined" || !navigator.clipboard) throw new Error("clipboard unavailable");
    await navigator.clipboard.writeText(text);
    toasts.success(labels.copied);
    return true;
  } catch {
    toasts.error(labels.failed);
    return false;
  }
}
