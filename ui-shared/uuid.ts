// A random UUID v4.
//
// `crypto.randomUUID` is only defined in secure contexts (https, localhost).
// A dmart served over plain http on a LAN — a common on-prem install — has
// `crypto` but no `randomUUID`, so the Math.random fallback is not legacy
// cover: it is what makes "auto" shortnames work there at all. cxb's copy had
// dropped it and marked the function deprecated; catalog carried two different
// fallbacks (lib/uuid.ts and helpers.generateUuidV4). This is the one copy.
export function generateUUID(): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}
