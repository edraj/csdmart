export type ModalSize = "xs" | "sm" | "md" | "lg" | "xl";

export const MODAL_SIZE = {
  confirm: "md",
  form: "lg",
  wide: "xl",
} as const satisfies Record<string, ModalSize>;

export type ModalSizePreset = keyof typeof MODAL_SIZE;
