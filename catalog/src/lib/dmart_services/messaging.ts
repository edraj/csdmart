import {
    ContentType,
    ResourceType,
    DmartScope,
    SortType,
} from "@edraj/tsdmart";
import {
    getEntity,
    createEntity,
    searchEntities,
    deleteEntity
} from "./core";
import { log } from "@/lib/logger";
import { PERSONAL_SPACE } from "@/lib/constants";
import { getCurrentScope } from "@/stores/user";

const PROTECTED_SUBPATH_BASE = "people";

/**
 * Get the protected subpath for a user
 * e.g., "people/username/protected"
 */
function getUserProtectedSubpath(userShortname: string): string {
    return `${PROTECTED_SUBPATH_BASE}/${userShortname}/protected`;
}

/**
 * Create a new direct message
 * Messages are stored in the recipient's protected folder in the personal space
 * If user A sends to user B, it's saved in /people/B/protected
 */
export async function createMessages(data: {
    content: string;
    sender: string;
    receiver: string;
    message_type?: string;
    timestamp?: string;
}) {
    const attributes = {
        is_active: true,
        relationships: [],
        tags: [],
        payload: {
            content_type: ContentType.json,
            body: {
                content: data.content,
                sender: data.sender,
                receiver: data.receiver,
                message_type: data.message_type || "text",
                timestamp: data.timestamp || new Date().toISOString(),
            },
        },
    };

    // Store message in recipient's protected folder
    // If A sends to B, store in /people/B/protected
    const targetSubpath = getUserProtectedSubpath(data.receiver);

    return await createEntity(
        PERSONAL_SPACE,
        targetSubpath,
        ResourceType.content,
        attributes,
        'auto'
    );
}

/**
 * Get messages between two users
 * - Fetches messages sent BY currentUser TO otherUser from /people/otherUser/protected?filter_by_owner=currentUser
 * - Fetches messages sent TO currentUser BY otherUser from /people/currentUser/protected?filter_by_owner=otherUser
 */
export async function getMessagesBetweenUsers(
    currentUserShortname: string,
    otherUserShortname: string,
    limit: number = 10,
    offset: number = 0
) {
    try {
        // 1. Fetch messages sent BY currentUser TO otherUser
        // These are stored in /people/otherUser/protected with owner_shortname = currentUser
        const sentByMeResponse = await searchEntities(
            PERSONAL_SPACE,
            getUserProtectedSubpath(otherUserShortname),
            `@owner_shortname:${currentUserShortname}`,
            limit,
            offset,
            "created_at",
            SortType.descending,
            DmartScope.managed,
            true,
            true,
            true
        );

        // 2. Fetch messages sent BY otherUser TO currentUser
        // These are stored in /people/currentUser/protected with owner_shortname = otherUser
        const receivedFromOtherResponse = await searchEntities(
            PERSONAL_SPACE,
            getUserProtectedSubpath(currentUserShortname),
            `@owner_shortname:${otherUserShortname}`,
            limit,
            offset,
            "created_at",
            SortType.descending,
            DmartScope.managed,
            true,
            true,
            true
        );

        // Combine and sort messages
        const sentByMe = sentByMeResponse?.status === "success" ? sentByMeResponse.records : [];
        const receivedFromOther = receivedFromOtherResponse?.status === "success" ? receivedFromOtherResponse.records : [];

        const allMessages = [...sentByMe, ...receivedFromOther];

        // Sort by created_at ascending (oldest first for conversation view)
        allMessages.sort((a, b) => {
            const dateA = new Date(a.attributes.created_at || 0).getTime();
            const dateB = new Date(b.attributes.created_at || 0).getTime();
            return dateA - dateB;
        });

        // Apply limit after combining
        const limitedMessages = allMessages.slice(0, limit);

        return {
            status: "success",
            records: limitedMessages,
        };
    } catch (err) {
        log.error("Error fetching messages between users:", err);
        return { status: "error", records: [] };
    }
}

/**
 * Get a single message by its shortname
 * Searches in both users' protected folders to find the message
 */
export async function getMessageByShortname(
    shortname: string,
    senderShortname?: string,
    receiverShortname?: string,
    subpath?: string
) {
    try {
        // Try to find the message in either user's protected folder
        const possibleLocations = [];

        if (subpath) {
            // If we know the exact subpath from a notification, try it first
            possibleLocations.push({
                space: PERSONAL_SPACE,
                subpath: subpath.startsWith("/") ? subpath.substring(1) : subpath,
            });
        }

        if (receiverShortname) {
            possibleLocations.push({
                space: PERSONAL_SPACE,
                subpath: getUserProtectedSubpath(receiverShortname),
            });
        }
        if (senderShortname) {
            possibleLocations.push({
                space: PERSONAL_SPACE,
                subpath: getUserProtectedSubpath(senderShortname),
            });
        }

        // Try each location
        for (const location of possibleLocations) {
            try {
                const record = await getEntity(
                    shortname,
                    location.space,
                    location.subpath,
                    ResourceType.content,
                    DmartScope.managed,
                    true,
                    true
                );

                if (record) {
                    const payload = (record as any).payload;
                    const body = payload?.body;

                    if (body) {
                        return {
                            id: record.shortname,
                            senderId: body.sender,
                            receiverId: body.receiver,
                            content: body.content,
                            timestamp: new Date((record as any).created_at || Date.now()),
                            messageType: body.message_type || "text",
                            isGroupMessage: false,
                            attachments: (record as any).attachments?.media || null,
                        };
                    }
                }
            } catch {
                // Continue to next location
            }
        }

        return null;
    } catch (error) {
        log.error("Failed to fetch message by shortname:", error);
        return null;
    }
}

// Conversation partners, cached per user for the session (review perf #9).
//
// The lookup used to fetch 1,000 messages with payload and attachments to
// collect a set of sender names, and ran again on every keystroke of the
// user search. Now: one request per user (metadata only — the sender of a
// message in my protected folder is its owner_shortname), the result kept
// until a new conversation starts, and the search box filters the cached
// list locally.
const partnersCache = new Map<string, Promise<string[]>>();

/** Forget the cached partner list (all users, or one). */
export function invalidateConversationPartners(currentUserShortname?: string): void {
    if (currentUserShortname === undefined) partnersCache.clear();
    else partnersCache.delete(currentUserShortname);
}

/**
 * Record a partner the page just learned about (a message sent to someone
 * new, or received from someone new over the websocket) without a refetch.
 */
export async function noteConversationPartner(
    currentUserShortname: string,
    partnerShortname: string,
): Promise<void> {
    if (!partnerShortname || partnerShortname === currentUserShortname) return;
    const pending = partnersCache.get(currentUserShortname);
    if (!pending) return;
    const partners = await pending.catch(() => null);
    if (partners && !partners.includes(partnerShortname)) partners.push(partnerShortname);
}

/**
 * Everyone who has sent the current user a direct message. Messages sent to
 * me live in /people/<me>/protected and their owner is the sender, so the
 * query needs neither payload nor attachments.
 */
export function getConversationPartners(currentUserShortname: string): Promise<string[]> {
    const hit = partnersCache.get(currentUserShortname);
    if (hit) return hit;

    const pending: Promise<string[]> = (async () => {
        try {
            const response = await searchEntities(
                PERSONAL_SPACE,
                getUserProtectedSubpath(currentUserShortname),
                "",
                1000,
                0,
                "created_at",
                SortType.descending,
                DmartScope.managed,
                false,
                false,
                true
            );

            const partners = new Set<string>();
            for (const record of response?.records ?? []) {
                const sender = record.attributes?.owner_shortname;
                if (sender && sender !== currentUserShortname) partners.add(sender);
            }
            return Array.from(partners);
        } catch (error) {
            log.error("Error fetching conversation partners:", error);
            partnersCache.delete(currentUserShortname);
            return [];
        }
    })();
    partnersCache.set(currentUserShortname, pending);
    return pending;
}

/**
 * Fetch contact messages (for admin contact page)
 * Kept for backward compatibility - uses applications space
 */
export async function fetchContactMessages() {
    try {
        return await searchEntities(
            "applications",
            "contacts",
            "",
            100,
            0,
            "created_at",
            SortType.descending,
            getCurrentScope(),
            true,
            true,
            true
        );
    } catch (err) {
        log.error("Error fetching contact messages:", err);
        return { status: "failed", records: [], attributes: {} };
    }
}

/**
 * Mark a message as replied (for contact form)
 */
export async function markMessageAsReplied(
    spaceName: string,
    subpath: string,
    parentShortname: string,
    replyContent: string
) {
    const attributes = {
        is_active: true,
        payload: {
            content_type: ContentType.json,
            body: {
                state: "replied",
                body: replyContent,
            },
        },
    };

    const targetSubpath = `${subpath}/${parentShortname}`.replaceAll("//", "/");

    return await createEntity(
        spaceName,
        targetSubpath,
        ResourceType.comment,
        attributes,
        "auto"
    );
}

/**
 * Create a generic item
 */
export async function createItem(
    itemName: string,
    itemType: string,
    spaceName: string,
    subpath: string = "/"
) {
    const attributes = {
        is_active: true,
        displayname: {
            en: itemName,
            ar: itemName,
        },
        description: {
            en: `Created via admin panel`,
            ar: `تم إنشاؤه عبر لوحة الإدارة`,
        },
    };

    return await createEntity(
        spaceName,
        subpath,
        itemType as ResourceType,
        attributes,
        "auto"
    );
}

/**
 * Delete an item
 */
export async function deleteItem(
    shortname: string,
    resourceType: string,
    subpath: string,
    spaceName: string
) {
    const result = await deleteEntity(
        shortname,
        spaceName,
        subpath || "/",
        resourceType as ResourceType
    );
    return result;
}
