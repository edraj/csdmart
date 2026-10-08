// Promise-returning confirmation, replacing every native confirm()/alert():
//
//   if (await confirm({ title: deleteTitle, body: cannotUndoText, variant: "danger" })) { ... }
//
// With an `action`, the dialog runs it on confirm, shows the loading state,
// keeps itself open with the error if it throws, and resolves true only after
// it succeeded:
//
//   const deleted = await confirm({ title, variant: "danger", action: () => deleteEntry(id) });
//
// Each call mounts a ConfirmDialog on document.body and unmounts it when the
// dialog closes; nothing needs to be placed in the page tree.

import { mount, unmount } from "svelte";
import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";

export interface ConfirmOptions {
  title: string;
  body?: string;
  variant?: "danger" | "primary";
  confirmLabel?: string;
  cancelLabel?: string;
  loadingLabel?: string;
  /** Runs on confirm; the dialog stays open with the error if it throws. */
  action?: () => Promise<unknown> | unknown;
}

export function confirm(options: ConfirmOptions): Promise<boolean> {
  if (typeof document === "undefined") return Promise.resolve(false);
  return new Promise<boolean>((resolve) => {
    const target = document.createElement("div");
    document.body.appendChild(target);
    let settled = false;

    const finish = (result: boolean) => {
      if (settled) return;
      settled = true;
      resolve(result);
      // Let the dialog's own close handlers run before it disappears.
      queueMicrotask(() => {
        void unmount(instance);
        target.remove();
      });
    };

    const instance = mount(ConfirmDialog, {
      target,
      props: {
        open: true,
        title: options.title,
        body: options.body,
        variant: options.variant ?? "primary",
        confirmLabel: options.confirmLabel,
        cancelLabel: options.cancelLabel,
        loadingLabel: options.loadingLabel,
        action: options.action,
        onConfirm: () => finish(true),
        onCancel: () => finish(false),
      },
    });
  });
}
