// Legacy names kept for the many existing call sites; all four delegate to
// lib/toast.ts so colours come from the design tokens (light and dark).
// New code should import { toasts } from "@/lib/toast".
import { toasts } from "@/lib/toast";

/** Success toast. */
export function successToastMessage(message: string) {
  toasts.success(message);
}

/** Warning toast. */
export function warningToastMessage(message: string) {
  toasts.warning(message);
}

/** Info toast. */
export function infoToastMessage(message: string) {
  toasts.info(message);
}

/**
 * Error toast.
 * @param noClose - If true, the toast stays until dismissed.
 */
export function errorToastMessage(message: string, noClose: boolean = false) {
  toasts.error(message, { sticky: noClose });
}
