import {
  bodyAs,
  type DirectMessageBody,
  type EntryRecord,
  type GroupBody,
  type GroupMessageBody,
  type LocalizedText,
  type UserAttributes,
} from "@/lib/types";

export function getDisplayName(displayname: LocalizedText | null | undefined): string | null {
  if (!displayname) return null;
  return displayname.en || displayname.ar || displayname.ku || null;
}

export function formatTime(date: Date | string): string {
  return new Date(date).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
  });
}

export function getPreviewUrl(file: File): string | null {
  if (file.type.startsWith("image/") || file.type.startsWith("video/")) {
    return URL.createObjectURL(file);
  }
  return null;
}

export function getFileIcon(file: File): string {
  if (file.type.startsWith("image/")) return "🖼️";
  if (file.type.startsWith("video/")) return "🎥";
  if (file.type.startsWith("audio/")) {
    if (file.name.includes("voice_message_")) return "🎤";
    return "🎵";
  }
  if (file.type.includes("pdf")) return "📄";
  if (file.type.includes("document") || file.type.includes("word")) return "📝";
  if (file.type.includes("spreadsheet") || file.type.includes("excel"))
    return "📊";
  return "📎";
}

export function formatFileSize(bytes: number): string {
  if (bytes === 0) return "0 Bytes";
  const k = 1024;
  const sizes = ["Bytes", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
}

export function formatRecordingDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60);
  const secs = seconds % 60;
  return `${mins}:${secs.toString().padStart(2, "0")}`;
}

export function scrollToBottom(chatContainer: HTMLElement | null): void {
  setTimeout(() => {
    if (chatContainer) {
      chatContainer.scrollTop = chatContainer.scrollHeight;
    }
  }, 100);
}

export interface MessageData {
  id: string;
  senderId: string;
  receiverId: string;
  content: string;
  timestamp: Date;
  isOwn: boolean;
  hasAttachments?: boolean;
  attachments?: EntryRecord[] | null;
  isUploading?: boolean;
  uploadFailed?: boolean;
}

export interface UserData {
  id: string;
  shortname: string;
  name: string;
  email?: string;
  avatar?: string | null;
  online: boolean;
  lastSeen: Date;
  roles: string[];
  isActive: boolean;
}

export interface GroupData {
  id: string;
  shortname: string;
  name: string;
  description?: string;
  avatar?: string | null;
  participants: string[];
  adminIds: string[];
  createdBy: string;
  createdAt: Date;
  isActive: boolean;
  isGroup: true;
}

export interface GroupMessageData extends Omit<MessageData, "receiverId"> {
  groupId: string;
  receiverId?: never;
}

export function transformUserRecord(record: EntryRecord<UserAttributes>): UserData {
  const attrs = record.attributes;
  return {
    id: record.shortname,
    shortname: record.shortname,
    name: getDisplayName(attrs.displayname) || attrs.email || record.shortname,
    email: attrs.email,
    avatar: attrs.social_avatar_url || null,
    online: false,
    lastSeen: new Date(attrs.updated_at || attrs.created_at || Date.now()),
    roles: attrs.roles || [],
    isActive: attrs.is_active !== false,
  };
}

export function transformMessageRecord(
  record: EntryRecord,
  currentUserShortname: string
): MessageData {
  const attachments = record.attachments?.media || null;
  const body = bodyAs<DirectMessageBody>(record.attributes.payload) ?? {};

  return {
    id: record.shortname,
    senderId: body.sender ?? "",
    receiverId: body.receiver ?? "",
    content: body.content ?? "",
    attachments: attachments,
    timestamp: new Date(record.attributes.created_at || Date.now()),
    isOwn: body.sender === currentUserShortname,
  };
}

export function transformGroupRecord(record: EntryRecord<UserAttributes>): GroupData {
  const attrs = record.attributes;
  const payload = bodyAs<GroupBody>(attrs.payload) ?? {};

  return {
    id: record.shortname,
    shortname: record.shortname,
    name: getDisplayName(attrs.displayname) || record.shortname,
    description: getDisplayName(attrs.description) || "",
    avatar: attrs.social_avatar_url || null,
    participants: payload.participants || [],
    adminIds: payload.adminIds || (payload.createdBy ? [payload.createdBy] : []),
    createdBy: payload.createdBy || "",
    createdAt: new Date(attrs.created_at || Date.now()),
    isActive: attrs.is_active !== false,
    isGroup: true,
  };
}

export function transformGroupMessageRecord(
  record: EntryRecord,
  currentUserShortname: string
): GroupMessageData {
  const attachments = record.attachments?.media || null;
  const body = bodyAs<GroupMessageBody>(record.attributes.payload) ?? {};

  return {
    id: record.shortname,
    senderId: body.sender ?? "",
    groupId: body.groupId ?? "",
    content: body.content ?? "",
    attachments: attachments,
    timestamp: new Date(record.attributes.created_at || Date.now()),
    isOwn: body.sender === currentUserShortname,
  };
}

export function getCacheKey(
  currentUserShortname: string,
  selectedUserShortname: string
): string {
  return `chat_${currentUserShortname}_${selectedUserShortname}`;
}

// Conversations are kept in memory for the session (review perf #31): the
// page used to JSON.stringify every conversation into localStorage on each
// message, never evicted it and left it behind after sign-out. Now a capped
// LRU map — the oldest conversation goes once there are more than
// MAX_CACHED_CONVERSATIONS, each one trimmed to its newest
// MAX_CACHED_MESSAGES — cleared by clearMessageCache() on sign-out.
export const MAX_CACHED_CONVERSATIONS = 30;
export const MAX_CACHED_MESSAGES = 200;

const LEGACY_STORAGE_PREFIXES = ["chat_", "group_chat_"];

const messageCache = new Map<string, MessageData[]>();

export function cacheMessages(cacheKey: string, messages: MessageData[]): void {
  messageCache.delete(cacheKey);
  messageCache.set(cacheKey, messages.slice(-MAX_CACHED_MESSAGES));
  while (messageCache.size > MAX_CACHED_CONVERSATIONS) {
    const oldest = messageCache.keys().next().value;
    if (oldest === undefined) break;
    messageCache.delete(oldest);
  }
}

export function getCachedMessages(cacheKey: string): MessageData[] {
  const hit = messageCache.get(cacheKey);
  if (!hit) return [];
  // Re-insert so the conversation counts as recently used.
  messageCache.delete(cacheKey);
  messageCache.set(cacheKey, hit);
  return hit.slice();
}

/** How many conversations are cached (for tests). */
export function cachedConversationCount(): number {
  return messageCache.size;
}

/**
 * Remove the conversations an earlier version persisted to localStorage, so
 * a visitor who signs out leaves no chat history in the browser.
 */
export function purgeLegacyMessageStorage(): void {
  if (typeof localStorage === "undefined") return;
  try {
    const stale: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key && LEGACY_STORAGE_PREFIXES.some((p) => key.startsWith(p))) stale.push(key);
    }
    stale.forEach((key) => localStorage.removeItem(key));
  } catch {
    // Storage may be unavailable (private mode, blocked site data).
  }
}

/** Forget every cached conversation (sign-out). */
export function clearMessageCache(): void {
  messageCache.clear();
  purgeLegacyMessageStorage();
}

/** The routing fields of a message as it arrives over the websocket. */
export interface MessageRouting {
  senderId?: unknown;
  receiverId?: unknown;
  groupId?: unknown;
}

export function isRelevantMessage(
  data: MessageRouting,
  selectedUserShortname: string,
  currentUserShortname: string
): boolean {
  return (
    (data.senderId === selectedUserShortname &&
      data.receiverId === currentUserShortname) ||
    (data.senderId === currentUserShortname &&
      data.receiverId === selectedUserShortname)
  );
}

export function sortMessagesByTimestamp(
  messages: MessageData[]
): MessageData[] {
  return messages.sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );
}

export function isRelevantGroupMessage(
  data: MessageRouting,
  groupId: string,
  currentUserShortname: string
): boolean {
  return data.groupId === groupId && data.senderId !== currentUserShortname;
}

export function getGroupCacheKey(
  currentUserShortname: string,
  groupId: string
): string {
  return `group_chat_${currentUserShortname}_${groupId}`;
}

export function isUserGroupAdmin(
  group: GroupData,
  userShortname: string
): boolean {
  return group.adminIds.includes(userShortname);
}

export function isUserGroupParticipant(
  group: GroupData,
  userShortname: string
): boolean {
  return group.participants.includes(userShortname);
}

export function canUserAccessGroup(
  group: GroupData,
  userShortname: string
): boolean {
  return (
    isUserGroupParticipant(group, userShortname) ||
    isUserGroupAdmin(group, userShortname)
  );
}

export function getGroupDisplayName(
  group: GroupData,
  fallback: string = "Unknown Group"
): string {
  return group.name || fallback;
}

export function formatGroupParticipantCount(count: number): string {
  if (count === 1) return "1 participant";
  return `${count} participants`;
}
