/**
 * Safe localStorage wrapper.
 * Handles environments where localStorage is unavailable (SSR, private mode, quota exceeded).
 */

const isAvailable = typeof localStorage !== "undefined";

export const storage = {
  get(key: string): string | null {
    if (!isAvailable) return null;
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },

  set(key: string, value: string): void {
    if (!isAvailable) return;
    try {
      localStorage.setItem(key, value);
    } catch {
      // Quota exceeded or access denied — fail silently
    }
  },

  remove(key: string): void {
    if (!isAvailable) return;
    try {
      localStorage.removeItem(key);
    } catch {
      // ignore
    }
  },

  getJson<T>(key: string, fallback: T): T {
    const raw = this.get(key);
    if (raw === null) return fallback;
    try {
      return JSON.parse(raw) as T;
    } catch {
      return fallback;
    }
  },

  setJson(key: string, value: unknown): void {
    this.set(key, JSON.stringify(value));
  },
};
