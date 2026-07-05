import { writable } from "svelte/store";
import { storage } from "@/lib/storage";

export const authToken = writable(storage.get("authToken") ?? "");
