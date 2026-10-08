<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import { user, type User } from "@/stores/user";
  import { ResourceType } from "@edraj/tsdmart";
  import { MESSAGES_SPACE } from "@/lib/constants";
  import { isEntryRecord, isJsonObject, recordsOf } from "@/lib/types";
  import {
    createMessages,
    getAllUsers,
    getMessagesBetweenUsers,
    getMessageByShortname,
    getConversationPartners,
    noteConversationPartner,
    getUsersByShortnames,
    attachAttachmentsToEntity,
    fetchOnlineUsers,
    createGroup,
    getUserGroups,
    createGroupMessage,
    getGroupMessages,
    getGroupMessageByShortname,
    updateGroup,
  } from "@/lib/dmart_services";
  import { _ } from "@/i18n";
  import {
    successToastMessage,
    errorToastMessage,
  } from "@/lib/toasts_messages";
  import MessengerAttachments from "@/components/MessengerAttachments.svelte";
  import ChatHeader from "@/components/messaging/ChatHeader.svelte";
  import ChatModeTabs from "@/components/messaging/ChatModeTabs.svelte";
  import UsersList from "@/components/messaging/UsersList.svelte";
  import GroupsList from "@/components/messaging/GroupsList.svelte";
  import MessageInput from "@/components/messaging/MessageInput.svelte";
  import GroupModal from "@/components/messaging/GroupModal.svelte";
  import {
    formatTime,
    getPreviewUrl,
    getFileIcon,
    formatFileSize,
    scrollToBottom,
    transformUserRecord,
    transformMessageRecord,
    getCacheKey,
    cacheMessages,
    getCachedMessages,
    sortMessagesByTimestamp,
    transformGroupRecord,
    transformGroupMessageRecord,
    isRelevantGroupMessage,
    getGroupCacheKey,
    isUserGroupAdmin,
    canUserAccessGroup,
    purgeLegacyMessageStorage,
    type GroupData,
    type GroupMessageData,
    type MessageAttachment,
    type MessageData,
    type UserData,
  } from "@/lib/utils/messagingUtils";
  import type { EntryRecord } from "@/lib/types";
  import { setTitle } from "@/lib/title";
  import { log } from "@/lib/logger";
  import { getWebSocketService, type WebSocketMessage } from "@/lib/services/websocket";
  import { wsConnected, wsStatus } from "@/stores/websocket";

  let isConnected = $derived($wsConnected);
  let connectionStatus = $state("");

  $effect(() => setTitle($_("messaging.title")));
  let removeWsListener: (() => void) | null = null;

  // Sync connection status from global WebSocket store
  $effect(() => {
    const status = $wsStatus;
    connectionStatus =
      status === "connected"
        ? $_("notifications_page.connection.connected")
        : status === "connecting"
          ? $_("notifications_page.connection.connecting")
          : $_("notifications_page.connection.disconnected");
  });

  let currentUser = $state<User | null>(null);
  // The signed-in user's shortname, "" until the session is known.
  const me = $derived(currentUser?.shortname ?? "");
  let users = $state<UserData[]>([]);
  let selectedUser = $state<UserData | null>(null);
  let isUsersLoading = $state(true);
  let showAllUsers = $state(true);
  let userSearchQuery = $state("");

  let groups = $state<GroupData[]>([]);
  let selectedGroup = $state<GroupData | null>(null);
  let isGroupsLoading = $state(true);

  let chatMode = $state("direct");
  let messages = $state<MessageData[]>([]);
  let groupMessages = $state<GroupMessageData[]>([]);
  let conversationMessages = new Map<string, MessageData[]>();
  let groupConversationMessages = new Map<string, GroupMessageData[]>();

  let currentMessage = $state("");
  let selectedAttachments = $state<File[]>([]);
  let isAttachmentLoading = $state(false);

  let isRecording = $state(false);
  let mediaRecorder: MediaRecorder | null = null;
  let audioChunks: Blob[] = [];
  let recordingDuration = $state(0);
  let recordingInterval: ReturnType<typeof setInterval> | null = null;
  let stream: MediaStream | null = null;

  let isMessagesLoading = $state(false);
  let isLoadingOlderMessages = $state(false);
  let hasMoreMessages = $state(true);
  let chatContainer = $state<HTMLElement | null>(null);

  let showGroupForm = $state(false);
  let showGroupEditForm = $state(false);
  let newGroupName = $state("");
  let newGroupDescription = $state("");
  let selectedGroupParticipants = $state<UserData[]>([]);
  let editGroupName = $state("");
  let editGroupDescription = $state("");
  let editGroupParticipants = $state<string[]>([]);
  let availableUsersForGroup = $state<UserData[]>([]);

  /** A websocket field read as text, or undefined when it is not one. */
  const text = (value: unknown): string | undefined =>
    typeof value === "string" ? value : undefined;

  /** The stored attachment records of a message, leaving out pending uploads. */
  function storedAttachments(list: MessageAttachment[] | null | undefined): EntryRecord[] {
    return (list ?? []).filter((item): item is EntryRecord => !(item instanceof File));
  }

  /** The files of a message still being uploaded. */
  function pendingFiles(list: MessageAttachment[] | null | undefined): File[] {
    return (list ?? []).filter((item): item is File => item instanceof File);
  }

  const MESSAGES_LIMIT = 100; // Increased limit since messages come from two sources


  onMount(async () => {
    // Conversations used to be persisted to localStorage; drop any left over.
    purgeLegacyMessageStorage();
    await initializeChat();
  });

  // Register WS listener reactively — handles cases where global WS
  // connects after the messaging page has already mounted
  $effect(() => {
    if ($wsConnected) {
      const ws = getWebSocketService();
      if (ws && !removeWsListener) {
        removeWsListener = ws.addMessageListener(handleRealtimeMessage);
      }
    }
  });

  $effect(() => {
    if ($user && !currentUser) {
      currentUser = $user;
    }
  });

  onDestroy(() => {
    removeWsListener?.();
    if (stream) {
      stream.getTracks().forEach((track) => track.stop());
    }
    if (recordingInterval) {
      clearInterval(recordingInterval);
    }
  });

  function handleScroll(event: Event) {
    const container = event.target as HTMLElement;
    if (
      container.scrollTop === 0 &&
      hasMoreMessages &&
      !isLoadingOlderMessages
    ) {
      loadOlderMessages();
    }
  }

  async function loadOlderMessages() {
    // With the new personal space storage model, pagination is more complex
    // as messages come from two sources. For now, we disable load-more functionality
    // and fetch a larger initial batch.
    // TODO: Implement proper cursor-based pagination for dual-source messages
    hasMoreMessages = false;
    return;
  }

  async function initializeChat() {
    try {
      currentUser = $user;

      if (!currentUser?.shortname) {
        connectionStatus = $_("messaging.waiting_for_user");
        return;
      }
      await Promise.all([loadUsers(), loadGroups()]);
    } catch (error) {
      log.error("[Lifecycle] initializeChat error:", error);
      connectionStatus = $_("messaging.toast_failed_initialize");
    }
  }

  async function loadUsers(search: string = "") {
    try {
      isUsersLoading = true;

      if (!currentUser?.shortname) {
        users = [];
        return;
      }

      // Server-side prefix search on shortname. Strip anything outside the
      // shortname charset so a stray ':' or '*' can't break the query syntax.
      const cleaned = search.trim().replace(/[^a-zA-Z0-9_-]/g, "");
      const searchClause = cleaned ? `@shortname:${cleaned}*` : "";

      // Fetch online users in parallel with user list
      const onlineUsersPromise = fetchOnlineUsers();

      let loadedUsers: UserData[] = [];

      if (showAllUsers) {
        const response = await getAllUsers(100, 0, searchClause);
        if (response.status === "success" && response.records) {
          loadedUsers = response.records
            .map(transformUserRecord)
            .filter(
              (user) => user.isActive && user.id !== me
            );
        }
      } else {
        const conversationPartners = await getConversationPartners(me);

        // Match the server-side prefix semantics for the conversations view
        // by filtering partner shortnames before fetching their records.
        const filteredPartners = cleaned
          ? conversationPartners.filter((p) =>
              p.toLowerCase().startsWith(cleaned.toLowerCase())
            )
          : conversationPartners;

        if (filteredPartners.length === 0) {
          users = [];
          return;
        }

        const response = await getUsersByShortnames(filteredPartners);

        if (response.status === "success" && response.records) {
          loadedUsers = response.records
            .map(transformUserRecord)
            .filter((user) => user.isActive);
        }
      }

      // Merge online status
      const onlineUsers = await onlineUsersPromise;
      users = loadedUsers
        .map((u) => ({ ...u, online: onlineUsers.has(u.shortname) }))
        .sort((a, b) => (a.online === b.online ? 0 : a.online ? -1 : 1));
    } catch (error) {
      errorToastMessage($_("messaging.toast_failed_load_users") + ": " + error);
      users = [];
    } finally {
      isUsersLoading = false;
    }
  }

  async function loadGroups() {
    try {
      isGroupsLoading = true;

      if (!currentUser?.shortname) {
        groups = [];
        return;
      }

      const response = await getUserGroups(me);
      if (response.status === "success" && response.records) {
        groups = response.records
          .map(transformGroupRecord)
          .filter(
            (group) => group.isActive && canUserAccessGroup(group, me)
          );
      } else {
        groups = [];
      }
    } catch (error) {
      errorToastMessage(
        $_("messaging.toast_failed_load_groups") + ": " + error
      );
      groups = [];
    } finally {
      isGroupsLoading = false;
    }
  }

  function selectGroup(group: GroupData) {
    selectedGroup = group;
    selectedUser = null;
    chatMode = "group";

    loadGroupMessages(group.id);
  }

  function selectUser(user: UserData) {
    selectedUser = user;
    selectedGroup = null;
    chatMode = "direct";
    loadConversation(user.shortname);
  }

  async function loadGroupMessages(groupId: string) {
    try {
      isMessagesLoading = true;
      hasMoreMessages = true;

      const cacheKey = getGroupCacheKey(me, groupId);
      const cachedMessages = getCachedMessages<GroupMessageData>(cacheKey);

      if (cachedMessages.length > 0) {
        groupMessages = cachedMessages;
        setTimeout(() => scrollToBottom(chatContainer), 100);
      }

      const response = await getGroupMessages(groupId, MESSAGES_LIMIT, 0);

      if (response && response.status === "success" && response.records) {
        const apiMessages = sortMessagesByTimestamp(
          recordsOf(response).map((record) =>
            transformGroupMessageRecord(record, me)
          )
        );

        // Merge with any cached real-time group messages
        const memoryCached = groupConversationMessages.get(groupId) || [];
        const localCached = getCachedMessages<GroupMessageData>(cacheKey);
        const allCached = [...memoryCached, ...localCached];
        const cachedById = new Map<string, GroupMessageData>();
        for (const msg of allCached) {
          if (!cachedById.has(msg.id)) {
            cachedById.set(msg.id, msg);
          }
        }

        const apiMessageIds = new Set(apiMessages.map((m) => m.id));
        const mergedMessages = sortMessagesByTimestamp([
          ...apiMessages,
          ...Array.from(cachedById.values()).filter((msg) => !apiMessageIds.has(msg.id)),
        ]);

        groupMessages = mergedMessages;
        groupConversationMessages.set(groupId, [...mergedMessages]);
        cacheMessages(cacheKey, mergedMessages);

        setTimeout(() => scrollToBottom(chatContainer), 100);
      }
    } catch (error) {
      errorToastMessage(
        $_("messaging.toast_failed_load_group_messages") + ": " + error
      );
    } finally {
      isMessagesLoading = false;
    }
  }

  async function sendGroupMessage() {
    if (!currentMessage.trim() && selectedAttachments.length === 0) {
      return;
    }
    const group = selectedGroup;
    if (!group || !me) {
      return;
    }

    const messageContent = currentMessage.trim() || "";
    const hasAttachments = selectedAttachments.length > 0;
    const tempId = `temp_group_${Date.now()}`;

    if (hasAttachments) {
      isAttachmentLoading = true;
    }

    const tempMessage: GroupMessageData = {
      id: tempId,
      senderId: me,
      groupId: group.id,
      content: messageContent || (hasAttachments ? "📎 attachment" : ""),
      timestamp: new Date(),
      isOwn: true,
      hasAttachments: hasAttachments,
      attachments: hasAttachments ? selectedAttachments : null,
      isUploading: hasAttachments,
    };

    groupMessages = [...groupMessages, tempMessage];
    scrollToBottom(chatContainer);

    currentMessage = "";
    const attachmentsToProcess = [...selectedAttachments];
    selectedAttachments = [];

    try {
      const groupMessageData = {
        groupId: group.id,
        sender: me,
        content: messageContent || (hasAttachments ? "attachment" : ""),
      };

      const persistedMessageId = await createGroupMessage(groupMessageData);

      if (persistedMessageId) {
        groupMessages = groupMessages.map((msg) =>
          msg.id === tempId
            ? { ...msg, id: persistedMessageId, isUploading: false }
            : msg
        );

        if (hasAttachments && attachmentsToProcess.length > 0) {
          try {
            // Attachments go next to the group message they belong to,
            // which createGroupMessage stores in the messages folder.
            const attachmentSpace = MESSAGES_SPACE;
            const attachmentSubpath = "messages";

            for (const attachment of attachmentsToProcess) {
              const attachmentResult = await attachAttachmentsToEntity(
                persistedMessageId,
                attachmentSpace,
                attachmentSubpath,
                attachment
              );

              if (!attachmentResult) {
                errorToastMessage(
                  $_("messaging.toast_attachment_failed", {
                    values: { name: attachment.name },
                  }) || `Failed to attach ${attachment.name}`
                );
              }
            }

            setTimeout(async () => {
              try {
                const messageData = await getGroupMessageByShortname(
                  persistedMessageId
                );

                if (messageData) {
                  const newMessage: GroupMessageData = {
                    id: messageData.id,
                    senderId: messageData.senderId,
                    groupId: messageData.groupId,
                    content: messageData.content,
                    timestamp: new Date(messageData.timestamp),
                    isOwn: true,
                    hasAttachments: !!messageData.attachments,
                    attachments: messageData.attachments,
                  };

                  groupMessages = groupMessages.map((msg) =>
                    msg.id === persistedMessageId ? newMessage : msg
                  );
                  groupConversationMessages.set(group.id, [
                    ...groupMessages,
                  ]);

                  const cacheKey = getGroupCacheKey(me, group.id);
                  cacheMessages(cacheKey, groupMessages);

                  scrollToBottom(chatContainer);
                }
              } catch (error) {
                log.error("Error refreshing group message:", error);
              }
            }, 1500);
          } catch (attachmentError) {
            errorToastMessage(
              $_("messaging.toast_attachment_error") + ": " + attachmentError
            );

            groupMessages = groupMessages.map((msg) =>
              msg.id === persistedMessageId
                ? { ...msg, isUploading: false, uploadFailed: true }
                : msg
            );
          }
        }

        groupConversationMessages.set(group.id, [...groupMessages]);
        const cacheKey = getGroupCacheKey(me, group.id);
        cacheMessages(cacheKey, groupMessages);

        const wsMessage: WebSocketMessage = {
          type: "message",
          messageId: persistedMessageId,
          senderId: me,
          groupId: group.id,
          content: messageContent || (hasAttachments ? "attachment" : ""),
          timestamp: tempMessage.timestamp.toISOString(),
          hasAttachments: hasAttachments,
          participants: group.participants,
        };

        const ws = getWebSocketService();
        if (ws) {
          await ws.send(wsMessage);
        }

        setTimeout(() => scrollToBottom(chatContainer), 100);
      } else {
        log.error("❌ [Group Message] API returned no response");
        groupMessages = groupMessages.filter((msg) => msg.id !== tempId);
      }
    } catch (error) {
      log.error("❌ [Group Message] Error sending message:", error);

      groupMessages = groupMessages.filter((msg) => msg.id !== tempId);
    } finally {
      isAttachmentLoading = false;
    }
  }

  async function createNewGroup() {
    if (!newGroupName.trim() || selectedGroupParticipants.length === 0) {
      errorToastMessage(
        $_("messaging.toast_group_creation_error") ||
          "Please provide group name and select participants"
      );
      return;
    }

    try {
      const participants = [
        me,
        ...selectedGroupParticipants.map((p) => p.shortname),
      ];

      const response = await createGroup({
        name: newGroupName.trim(),
        description: newGroupDescription.trim(),
        participants: participants,
        createdBy: me,
      });

      if (response) {
        successToastMessage(
          $_("messaging.toast_group_created") || "Group created successfully"
        );
        showGroupForm = false;
        newGroupName = "";
        newGroupDescription = "";
        selectedGroupParticipants = [];
        await loadGroups();
      }
    } catch (error) {
      errorToastMessage(
        $_("messaging.toast_failed_create_group") + ": " + error
      );
    }
  }

  async function openGroupEditForm() {
    if (
      !selectedGroup ||
      !isUserGroupAdmin(selectedGroup, me)
    ) {
      errorToastMessage("Only group admins can edit group settings");
      return;
    }

    editGroupName = selectedGroup.name;
    editGroupDescription = selectedGroup.description || "";
    editGroupParticipants = selectedGroup.participants || [];

    try {
      const response = await getAllUsers();
      if (response.status === "success" && response.records) {
        availableUsersForGroup = response.records
          .map(transformUserRecord)
          .filter(
            (user) =>
              user.isActive &&
              user.id !== me &&
              !editGroupParticipants.includes(user.shortname)
          );
      }
    } catch (error) {
      log.error("Failed to load users for group editing:", error);
    }

    showGroupEditForm = true;
  }

  async function updateGroupDetails() {
    if (!editGroupName.trim()) {
      errorToastMessage("Group name is required");
      return;
    }

    if (!selectedGroup) return;

    try {
      const updateData = {
        name: editGroupName.trim(),
        description: editGroupDescription.trim(),
        participants: editGroupParticipants,
      };

      const success = await updateGroup(selectedGroup.shortname, updateData);

      if (success) {
        successToastMessage("Group updated successfully");
        showGroupEditForm = false;

        selectedGroup = {
          ...selectedGroup,
          name: editGroupName.trim(),
          description: editGroupDescription.trim(),
          participants: editGroupParticipants,
        };

        await loadGroups();
      } else {
        errorToastMessage("Failed to update group");
      }
    } catch (error) {
      errorToastMessage("Failed to update group: " + error);
    }
  }

  function addParticipantToGroup(user: UserData) {
    if (!editGroupParticipants.includes(user.shortname)) {
      editGroupParticipants = [...editGroupParticipants, user.shortname];
      availableUsersForGroup = availableUsersForGroup.filter(
        (u) => u.shortname !== user.shortname
      );
    }
  }

  function removeParticipantFromGroup(userShortname: string) {
    if (userShortname === me) {
      errorToastMessage("You cannot remove yourself from the group");
      return;
    }

    editGroupParticipants = editGroupParticipants.filter(
      (p) => p !== userShortname
    );

    const userToAdd = users.find((u) => u.shortname === userShortname);
    if (
      userToAdd &&
      !availableUsersForGroup.some((u) => u.shortname === userShortname)
    ) {
      availableUsersForGroup = [...availableUsersForGroup, userToAdd];
    }
  }

  /** The attachment records a websocket frame carries, or null when none. */
  function frameAttachments(value: unknown): EntryRecord[] | null {
    const list = Array.isArray(value) ? value.filter(isEntryRecord) : [];
    return list.length > 0 ? list : null;
  }

  /** A websocket frame's send time, now when it has none. */
  function frameTimestamp(value: unknown): Date {
    const sentAt = typeof value === "string" || typeof value === "number" ? value : undefined;
    return new Date(sentAt || Date.now());
  }

  function handleRealtimeMessage(data: WebSocketMessage) {

    if (data.type === "connection_response") {
      return;
    }

    const notice = isJsonObject(data.message) ? data.message : null;

    // Handle subscription confirmations (from channel_subscribe)
    if (data.type === "notification_subscription" && notice?.status === "success" && !notice?.action_type) {
      return;
    }

    // Handle plugin broadcast notifications (new content created/updated)
    if (data.type === "notification_subscription" && notice?.action_type) {
      const createdShortname = text(notice.shortname);
      if (notice.action_type === "create" && createdShortname) {
        const ownerShortname = text(notice.owner_shortname);
        fetchMessageByShortname(createdShortname, ownerShortname, undefined, text(notice.subpath));
      }
      return;
    }

    const senderId = text(data.senderId) ?? "";
    const receiverId = text(data.receiverId) ?? "";
    const groupId = text(data.groupId) ?? "";
    const messageId = text(data.messageId);
    const hasAttachments = data.hasAttachments === true;

    // Handle direct real-time messages (type: "message")
    if (data.type === "message") {

      // Skip messages sent by current user (already shown via optimistic UI)
      if (senderId === me) {
        return;
      }

      // Group messages
      if (groupId) {
        const isRelevant = isRelevantGroupMessage(
          { senderId, groupId },
          groupId,
          me
        );

        if (isRelevant) {
          const newGroupMessage: GroupMessageData = {
            id: messageId || `msg_group_${Date.now()}`,
            senderId,
            groupId,
            content: text(data.content) || "",
            timestamp: frameTimestamp(data.timestamp),
            isOwn: false,
            hasAttachments,
            attachments: frameAttachments(data.attachments),
          };

          // Update cache for this group even if not currently selected
          updateGroupMessageCache(groupId, newGroupMessage);

          // Update UI only if this group is currently selected
          if (selectedGroup && groupId === selectedGroup.id && chatMode === "group") {
            const messageExists = groupMessages.some(
              (msg) => msg.id === newGroupMessage.id
            );
            if (!messageExists) {
              groupMessages = [...groupMessages, newGroupMessage];
              scrollToBottom(chatContainer);
            }
          }
        }
        return;
      }

      // Direct messages (no groupId)
      if (senderId && receiverId) {
        if (receiverId !== me && senderId !== me) {
          return;
        }
        const partnerShortname = senderId;

        if (hasAttachments && messageId) {
          const tempMessage: MessageData = {
            id: `temp_attachment_${messageId}`,
            senderId,
            receiverId,
            content: text(data.content) || "📎 Attachment",
            timestamp: frameTimestamp(data.timestamp),
            isOwn: false,
            hasAttachments: true,
            attachments: null,
            isUploading: true,
          };

          // Update cache even if not currently selected
          updateDirectMessageCache(partnerShortname, tempMessage);

          // Update UI if currently viewing this conversation
          if (selectedUser?.shortname === partnerShortname && chatMode === "direct") {
            const messageExists = messages.some(
              (msg) => msg.id === messageId || msg.id === tempMessage.id
            );
            if (!messageExists) {
              messages = [...messages, tempMessage];
              scrollToBottom(chatContainer);
            }
          }

          setTimeout(async () => {
            try {
              const messageData = await getMessageByShortname(
                messageId,
                senderId,
                me
              );

              if (messageData) {
                const newMessage: MessageData = {
                  id: messageData.id,
                  senderId: messageData.senderId,
                  receiverId: messageData.receiverId,
                  content: messageData.content,
                  timestamp: new Date(messageData.timestamp),
                  isOwn: false,
                  hasAttachments: !!messageData.attachments,
                  attachments: messageData.attachments,
                };

                // Update cache for this conversation
                updateDirectMessageCache(partnerShortname, newMessage);

                // Update UI if currently viewing this conversation
                if (selectedUser?.shortname === partnerShortname && chatMode === "direct") {
                  messages = messages.map((msg) =>
                    msg.id === tempMessage.id || msg.id === newMessage.id
                      ? newMessage
                      : msg
                  );
                  scrollToBottom(chatContainer);
                }
              }
            } catch (error) {
              log.error("Error fetching attachment message:", error);
              if (selectedUser?.shortname === partnerShortname && chatMode === "direct") {
                messages = messages.filter((msg) => msg.id !== tempMessage.id);
              }
              // Also remove from cache
              const cached = conversationMessages.get(partnerShortname) || [];
              const remaining = cached.filter((msg) => msg.id !== tempMessage.id);
              conversationMessages.set(partnerShortname, remaining);
              const cacheKey = getCacheKey(me, partnerShortname);
              cacheMessages(cacheKey, remaining);
            }
          }, 1000);

          return;
        }

        const newMessage: MessageData = {
          id: messageId || `ws_${Date.now()}`,
          senderId,
          receiverId,
          content: text(data.content) || "",
          timestamp: frameTimestamp(data.timestamp),
          isOwn: false,
          hasAttachments: false,
          attachments: null,
        };

        // Update cache for this conversation even if not currently selected
        updateDirectMessageCache(partnerShortname, newMessage);

        // Update UI only if currently viewing this conversation
        if (selectedUser?.shortname === partnerShortname && chatMode === "direct") {
          const messageExists = messages.some(
            (msg) => msg.id === newMessage.id
          );
          if (!messageExists) {
            messages = [...messages, newMessage];
            scrollToBottom(chatContainer);
          }
        }
      }
      return;
    }

    // Handle group_message type (alternative format)
    if (data.type === "group_message") {
      if (senderId === me) {
        return;
      }
      const isRelevant = isRelevantGroupMessage(
        { senderId, groupId },
        groupId,
        me
      );

      if (isRelevant) {
        const newGroupMessage: GroupMessageData = {
          id: messageId || `msg_group_${Date.now()}`,
          senderId,
          groupId,
          content: text(data.content) || "",
          timestamp: frameTimestamp(data.timestamp),
          isOwn: false,
          hasAttachments,
          attachments: frameAttachments(data.attachments),
        };

        // Update cache for this group even if not currently selected
        updateGroupMessageCache(groupId, newGroupMessage);

        // Update UI only if this group is currently selected
        if (selectedGroup && groupId === selectedGroup.id && chatMode === "group") {
          const messageExists = groupMessages.some(
            (msg) => msg.id === newGroupMessage.id
          );
          if (!messageExists) {
            groupMessages = [...groupMessages, newGroupMessage];
            scrollToBottom(chatContainer);
          }
        }
      }
      return;
    }

  }

  function updateDirectMessageCache(partnerShortname: string, newMessage: MessageData) {
    // A message to or from someone new makes them a conversation partner
    // without refetching the partner list (perf #9).
    if (me) void noteConversationPartner(me, partnerShortname);
    const existingMessages = conversationMessages.get(partnerShortname) || [];
    const messageExists = existingMessages.some((msg) => msg.id === newMessage.id);
    if (!messageExists) {
      const updatedMessages = sortMessagesByTimestamp([...existingMessages, newMessage]);
      conversationMessages.set(partnerShortname, updatedMessages);
      const cacheKey = getCacheKey(me, partnerShortname);
      cacheMessages(cacheKey, updatedMessages);
    }
  }

  function updateGroupMessageCache(groupId: string, newMessage: GroupMessageData) {
    const existingMessages = groupConversationMessages.get(groupId) || [];
    const messageExists = existingMessages.some((msg) => msg.id === newMessage.id);
    if (!messageExists) {
      const updatedMessages = sortMessagesByTimestamp([...existingMessages, newMessage]);
      groupConversationMessages.set(groupId, updatedMessages);
      const cacheKey = getGroupCacheKey(me, groupId);
      cacheMessages(cacheKey, updatedMessages);
    }
  }

  // Direct messages only: getMessageByShortname reads the personal-space
  // folders, and the group messages it would otherwise reach arrive over the
  // websocket as "message" frames carrying a groupId.
  async function fetchMessageByShortname(messageShortname: string, senderShortname?: string, receiverShortname?: string, subpath?: string) {
    try {

      const sender = senderShortname;
      const receiver = receiverShortname || me;

      const messageData = await getMessageByShortname(messageShortname, sender, receiver, subpath);

      if (!messageData) {
        return;
      }


      // Skip messages sent by current user (already shown via optimistic UI)
      if (messageData.senderId === me) {
        return;
      }

      // Handle direct messages
      const partnerShortname = messageData.senderId;
      const newMessage: MessageData = {
        id: messageData.id,
        senderId: messageData.senderId,
        receiverId: messageData.receiverId,
        content: messageData.content || "",
        timestamp: new Date(messageData.timestamp || Date.now()),
        isOwn: false,
        hasAttachments: !!messageData.attachments,
        attachments: messageData.attachments,
      };

      updateDirectMessageCache(partnerShortname, newMessage);

      if (selectedUser?.shortname === partnerShortname && chatMode === "direct") {
        const messageExists = messages.some(
          (msg) => msg.id === newMessage.id
        );
        if (!messageExists) {
          messages = [...messages, newMessage];
          scrollToBottom(chatContainer);
        }
      }
    } catch (error) {
      log.error("❌ [FetchMsg] Error fetching message:", error);
    }
  }

  function getUserDisplayName(shortname: string) {
    const user = users.find((u) => u.shortname === shortname);
    return user ? user.name || shortname : shortname;
  }

  async function loadConversation(userShortname: string) {
    if (!selectedUser) return;

    isMessagesLoading = true;
    hasMoreMessages = true;

    try {
      const response = await getMessagesBetweenUsers(
        me,
        userShortname,
        MESSAGES_LIMIT,
        0
      );

      let apiMessages: MessageData[] = [];
      if (response && response.status === "success" && response.records) {
        apiMessages = sortMessagesByTimestamp(
          recordsOf(response).map((record) =>
            transformMessageRecord(record, me)
          )
        );

        if (apiMessages.length < MESSAGES_LIMIT) {
          hasMoreMessages = false;
        }
      } else {
        hasMoreMessages = false;
      }

      // Merge with any cached real-time messages that may have arrived
      const cachedMessages = conversationMessages.get(userShortname) || [];
      const localCachedMessages = getCachedMessages<MessageData>(
        getCacheKey(me, userShortname)
      );

      const allCached = [...cachedMessages, ...localCachedMessages];
      const cachedById = new Map<string, MessageData>();
      for (const msg of allCached) {
        if (!cachedById.has(msg.id)) {
          cachedById.set(msg.id, msg);
        }
      }

      // API messages take precedence, but keep any cached messages not in API response
      const apiMessageIds = new Set(apiMessages.map((m) => m.id));
      const mergedMessages = sortMessagesByTimestamp([
        ...apiMessages,
        ...Array.from(cachedById.values()).filter((msg) => !apiMessageIds.has(msg.id)),
      ]);

      messages = mergedMessages;
      conversationMessages.set(userShortname, [...messages]);

      const cacheKey = getCacheKey(me, userShortname);
      cacheMessages(cacheKey, messages);
    } catch {
      messages = conversationMessages.get(userShortname) || [];

      if (messages.length === 0) {
        const cacheKey = getCacheKey(me, userShortname);
        messages = getCachedMessages<MessageData>(cacheKey);
      }
    } finally {
      isMessagesLoading = false;
      scrollToBottom(chatContainer);
    }
  }

  async function sendMessage() {
    const recipient = selectedUser;
    if (
      (!currentMessage.trim() && selectedAttachments.length === 0) ||
      !recipient ||
      !isConnected
    ) {
      return;
    }

    const messageContent = currentMessage.trim() || "";
    const hasAttachments = selectedAttachments.length > 0;
    const tempId = `temp_${Date.now()}`;

    if (hasAttachments) {
      isAttachmentLoading = true;
    }

    const newMessage: MessageData = {
      id: tempId,
      senderId: me,
      receiverId: recipient.shortname,
      content: messageContent || (hasAttachments ? "attachment" : ""),
      timestamp: new Date(),
      isOwn: true,
      hasAttachments: hasAttachments,
      attachments: hasAttachments ? selectedAttachments : null,
      isUploading: hasAttachments,
    };

    messages = [...messages, newMessage];
    scrollToBottom(chatContainer);

    currentMessage = "";
    const attachmentsToProcess = [...selectedAttachments];
    selectedAttachments = [];

    try {
      const messageData = {
        content: messageContent || (hasAttachments ? "attachment" : ""),
        sender: me,
        receiver: recipient.shortname,
        message_type: hasAttachments ? "attachment" : "text",
        timestamp: new Date().toISOString(),
      };

      const persistedMessageId = await createMessages(messageData);

      if (persistedMessageId) {
        messages = messages.map((msg) =>
          msg.id === tempId
            ? { ...msg, id: persistedMessageId, isUploading: false }
            : msg
        );

        if (hasAttachments && attachmentsToProcess.length > 0) {
          try {
            for (const attachment of attachmentsToProcess) {
              const attachmentResult = await attachAttachmentsToEntity(
                persistedMessageId,
                "messages",
                "messages",
                attachment
              );

              if (!attachmentResult) {
                errorToastMessage(
                  $_("messaging.toast_attachment_failed", {
                    values: { name: attachment.name },
                  }) || `Failed to attach ${attachment.name}`
                );
              }
            }

            setTimeout(async () => {
              try {
                const messageData = await getMessageByShortname(
                  persistedMessageId,
                  me,
                  recipient.shortname
                );

                if (messageData) {
                  const newMessage: MessageData = {
                    id: messageData.id,
                    senderId: messageData.senderId,
                    receiverId: messageData.receiverId,
                    content: messageData.content,
                    timestamp: new Date(messageData.timestamp),
                    isOwn: true,
                    hasAttachments: !!messageData.attachments,
                    attachments: messageData.attachments,
                  };

                  messages = messages.map((msg) =>
                    msg.id === persistedMessageId ? newMessage : msg
                  );
                  conversationMessages.set(recipient.shortname, [
                    ...messages,
                  ]);

                  const cacheKey = getCacheKey(me, recipient.shortname);
                  cacheMessages(cacheKey, messages);

                  scrollToBottom(chatContainer);
                }
              } catch (error) {
                log.error("Error refreshing message:", error);
              }
            }, 1500);
          } catch (attachmentError) {
            errorToastMessage(
              $_("messaging.toast_attachment_error") + ": " + attachmentError
            );

            messages = messages.map((msg) =>
              msg.id === persistedMessageId
                ? { ...msg, isUploading: false, uploadFailed: true }
                : msg
            );
          }
        }

        const wsMessage: WebSocketMessage = {
          type: "message",
          senderId: me,
          receiverId: recipient.shortname,
          content: messageContent || (hasAttachments ? "attachment" : ""),
          timestamp: new Date().toISOString(),
          messageId: persistedMessageId,
          hasAttachments: hasAttachments,
        };

        const wsRef = getWebSocketService();
        if (wsRef) {
          await wsRef.send(wsMessage);
        } else {
          log.warn("[SendMsg] No WS service, skipping WS send");
        }
      } else {
        messages = messages.filter((msg) => msg.id !== tempId);
      }
    } catch {
      messages = messages.filter((msg) => msg.id !== tempId);
    } finally {
      isAttachmentLoading = false;
    }

    if (!hasAttachments) {
      const conversationKey = recipient.shortname;
      const updatedMessages = messages.filter((msg) => msg.id !== tempId);

      if (updatedMessages.length > 0) {
        conversationMessages.set(conversationKey, updatedMessages);

        const cacheKey = getCacheKey(me, recipient.shortname);
        cacheMessages(cacheKey, updatedMessages);
      }
    }
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      if (chatMode === "group" && selectedGroup) {
        sendGroupMessage();
      } else if (chatMode === "direct" && selectedUser) {
        sendMessage();
      }
    }
  }

  function toggleUserView() {
    showAllUsers = !showAllUsers;
    loadUsers(userSearchQuery);
  }

  function handleFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    selectedAttachments = [...selectedAttachments, ...files];
    input.value = "";
  }

  function removeAttachment(index: number) {
    selectedAttachments = selectedAttachments.filter((_, i) => i !== index);
  }

  async function startVoiceRecording() {
    try {
      const audioStream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
      });
      stream = audioStream;

      const mimeTypes = [
        "audio/mp4;codecs=mp4a.40.2",
        "audio/mpeg",
        "audio/wav",
        "audio/mp4",
        "audio/webm;codecs=opus",
        "audio/webm",
      ];

      let selectedMimeType = "";
      for (const mimeType of mimeTypes) {
        if (MediaRecorder.isTypeSupported(mimeType)) {
          selectedMimeType = mimeType;
          break;
        }
      }

      if (!selectedMimeType) {
        throw new Error("No supported audio format found");
      }

      const recorder = new MediaRecorder(audioStream, {
        mimeType: selectedMimeType,
        audioBitsPerSecond: 128000,
      });
      mediaRecorder = recorder;

      audioChunks = [];
      recordingDuration = 0;

      recorder.ondataavailable = (event: BlobEvent) => {
        if (event.data.size > 0) {
          audioChunks.push(event.data);
        }
      };

      recorder.onstop = () => {
        const audioBlob = new Blob(audioChunks, { type: selectedMimeType });

        let fileExtension = "mp3";
        let finalMimeType = selectedMimeType;

        if (selectedMimeType.includes("webm")) {
          fileExtension = "mp3";
          finalMimeType = "audio/mpeg";
        } else if (selectedMimeType.includes("mp4")) {
          fileExtension = "mp3";
          finalMimeType = "audio/mpeg";
        } else if (selectedMimeType.includes("wav")) {
          fileExtension = "wav";
          finalMimeType = "audio/wav";
        }

        const fileName = `voice_message_${Date.now()}.${fileExtension}`;

        const audioFile = new File([audioBlob], fileName, {
          type: finalMimeType,
          lastModified: Date.now(),
        });

        selectedAttachments = [...selectedAttachments, audioFile];

        if (stream) {
      stream.getTracks().forEach((track) => track.stop());
          stream = null;
        }
      };

      recorder.start();
      isRecording = true;

      recordingInterval = setInterval(() => {
        recordingDuration++;
      }, 1000);
    } catch {
      isRecording = false;

      if (stream) {
        stream.getTracks().forEach((track) => track.stop());
        stream = null;
      }
    }
  }

  function stopVoiceRecording() {
    if (mediaRecorder && mediaRecorder.state === "recording") {
      mediaRecorder.stop();
    }

    isRecording = false;

    if (recordingInterval) {
      clearInterval(recordingInterval);
      recordingInterval = null;
    }
  }

  function cancelVoiceRecording() {
    if (mediaRecorder && mediaRecorder.state === "recording") {
      mediaRecorder.stop();
    }

    isRecording = false;
    recordingDuration = 0;
    audioChunks = [];

    if (recordingInterval) {
      clearInterval(recordingInterval);
      recordingInterval = null;
    }

    if (stream) {
      stream.getTracks().forEach((track) => track.stop());
      stream = null;
    }
  }
</script>

<div class="chat-container">
  <ChatHeader {isConnected} {connectionStatus} />

  <div class="chat-content">
    <!-- Users Sidebar -->
    <div class="users-sidebar">
      <ChatModeTabs
        {chatMode}
        onModeChange={(mode) => (chatMode = mode)}
        usersCount={users.length}
        groupsCount={groups.length}
      />

      {#if chatMode === "direct"}
        <UsersList
          {users}
          selectedUserId={selectedUser?.shortname}
          isLoading={isUsersLoading}
          {showAllUsers}
          onUserSelect={selectUser}
          onToggleView={toggleUserView}
          onRefresh={() => loadUsers(userSearchQuery)}
          onSearch={(q) => {
            userSearchQuery = q;
            loadUsers(q);
          }}
        />
      {:else}
        <GroupsList
          {groups}
          selectedGroupId={selectedGroup?.id}
          isLoading={isGroupsLoading}
          onGroupSelect={selectGroup}
          onCreateGroup={() => (showGroupForm = true)}
          onRefresh={loadGroups}
        />
      {/if}
    </div>

    <!-- Chat Area -->
    <div class="chat-area">
      {#if selectedUser && chatMode === "direct"}
        <!-- Direct Chat Header -->
        <div class="chat-user-header">
          <div class="chat-user-info">
            <div class="user-avatar small mx-3">
              {#if selectedUser.avatar}
                <img loading="lazy" decoding="async"
                  src={selectedUser.avatar || "/placeholder.svg"}
                  alt={selectedUser.name}
                />
              {:else}
                <div class="avatar-placeholder">
                  {selectedUser.name.charAt(0).toUpperCase()}
                </div>
              {/if}
              <div
                class="online-indicator small"
                class:online={selectedUser.online}
              ></div>
            </div>
            <div>
              <div class="chat-user-name">{selectedUser.name}</div>
              <div class="chat-user-status">
                {#if selectedUser.online}
                  <span class="online-text">{$_("messaging.online")}</span>
                {/if}
              </div>
            </div>
          </div>
        </div>

        <div
          class="messages-container"
          bind:this={chatContainer}
          onscroll={handleScroll}
        >
          {#if isLoadingOlderMessages}
            <div class="loading-older-messages">
              <div class="loading-spinner"></div>
              <span>{$_("messaging.loading_older_messages")}</span>
            </div>
          {/if}

          {#if isMessagesLoading}
            <div class="loading">{$_("messaging.loading_messages")}</div>
          {:else if messages.length === 0}
            <div class="no-messages">
              <p>{$_("messaging.no_messages_yet")}</p>
            </div>
          {:else}
            {#each messages as message (message.id)}
              <div class="message" class:own={message.isOwn}>
                <div class="message-content">
                  {#if message.content && message.content !== "attachment"}
                    <div class="message-text">{message.content}</div>
                  {/if}

                  {#if message.isUploading}
                    <div class="upload-status">
                      <div class="upload-spinner"></div>
                      <span>Uploading...</span>
                    </div>
                  {:else if message.uploadFailed}
                    <div class="upload-failed">
                      <span class="error-icon">⚠️</span>
                      <span>{$_("labels.upload_failed")}</span>
                    </div>
                  {/if}

                  {#if storedAttachments(message.attachments).length > 0}
                    <!-- 
                      Attachments are stored in the recipient's personal space:
                      - If message.isOwn (I sent it), attachment is in receiver's space
                      - If !message.isOwn (I received it), attachment is in my space
                    -->
                    {@const attachmentSpace = "personal"}
                    {@const attachmentSubpath = message.isOwn 
                      ? `people/${selectedUser.shortname}/protected`
                      : `people/${me}/protected`}
                    <MessengerAttachments
                      attachments={storedAttachments(message.attachments)}
                      resource_type={ResourceType.media}
                      space_name={attachmentSpace}
                      subpath={attachmentSubpath}
                      parent_shortname={message.id}
                      isOwner={message.isOwn}
                    />
                  {/if}

                  <!-- Show temp attachments for pending messages -->
                  {#if message.hasAttachments && pendingFiles(message.attachments).length > 0 && !message.isUploading}
                    <div class="message-attachments">
                      {#each pendingFiles(message.attachments) as file, i (i)}
                        <div class="attachment-item temp-attachment">
                          {#if file.type.startsWith("audio/") && file.name.includes("voice_message_")}
                            <!-- Voice Message Preview -->
                            <div class="voice-message-preview">
                              <div class="voice-message-icon">🎤</div>
                              <div class="voice-message-info">
                                <div class="voice-message-label">
                                  Voice Message
                                </div>
                                <div class="file-size">
                                  {formatFileSize(file.size)}
                                </div>
                              </div>
                              <audio controls class="voice-audio-control">
                                <source
                                  src={getPreviewUrl(file)}
                                  type={file.type}
                                />
                                Your browser does not support the audio element.
                              </audio>
                            </div>
                          {:else if getPreviewUrl(file)}
                            <img loading="lazy" decoding="async"
                              src={getPreviewUrl(file)}
                              alt={file.name}
                              class="attachment-image"
                            />
                          {:else}
                            <div class="attachment-file">
                              <div class="file-icon-display">
                                {getFileIcon(file)}
                              </div>
                              <div class="file-details">
                                <div class="file-name">{file.name}</div>
                                <div class="file-size">
                                  {formatFileSize(file.size)}
                                </div>
                              </div>
                            </div>
                          {/if}
                        </div>
                      {/each}
                    </div>
                  {/if}

                  <div class="message-time">
                    {formatTime(message.timestamp)}
                  </div>
                </div>
              </div>
            {/each}
          {/if}
        </div>

        <MessageInput
          {currentMessage}
          {selectedAttachments}
          {isConnected}
          {isRecording}
          {isAttachmentLoading}
          {recordingDuration}
          placeholder={$_("messaging.type_a_message")}
          onSend={sendMessage}
          onFileSelect={handleFileSelect}
          onKeydown={handleKeydown}
          onStartRecording={startVoiceRecording}
          onStopRecording={stopVoiceRecording}
          onCancelRecording={cancelVoiceRecording}
          onRemoveAttachment={removeAttachment}
          onMessageChange={(value) => (currentMessage = value)}
        />
      {:else if selectedGroup && chatMode === "group"}
        <div class="chat-group-header">
          <div class="chat-group-info">
            <div class="group-avatar small">
              {#if selectedGroup.avatar}
                <img loading="lazy" decoding="async" src={selectedGroup.avatar} alt={selectedGroup.name} />
              {:else}
                <div class="avatar-placeholder group">
                  {selectedGroup.name.charAt(0).toUpperCase()}
                </div>
              {/if}
            </div>
            <div>
              <div class="chat-group-name">{selectedGroup.name}</div>
              <div class="chat-group-status">
                {selectedGroup.participants.length}
                {$_("messaging.participants")}
                {#if isUserGroupAdmin(selectedGroup, me)}
                  • {$_("messaging.admin")}
                {/if}
                <div class="group-participants-preview">
                  {selectedGroup.participants
                    .slice(0, 3)
                    .map(getUserDisplayName)
                    .join(", ")}
                  {#if selectedGroup.participants.length > 3}
                    and {selectedGroup.participants.length - 3} more
                  {/if}
                </div>
              </div>
            </div>
          </div>
          {#if isUserGroupAdmin(selectedGroup, me)}
            <div class="group-header-actions">
              <button
                class="edit-group-btn"
                onclick={openGroupEditForm}
                aria-label={$_("messaging.edit_group_settings")}
                title={$_("messaging.edit_group_settings")}
              >
                ✏️
              </button>
            </div>
          {/if}
        </div>

        <div
          class="messages-container"
          bind:this={chatContainer}
          onscroll={handleScroll}
        >
          {#if isLoadingOlderMessages}
            <div class="loading-older-messages">
              <div class="loading-spinner"></div>
              <span>{$_("messaging.loading_older_messages")}</span>
            </div>
          {/if}

          {#if isMessagesLoading}
            <div class="loading">{$_("messaging.loading_messages")}</div>
          {:else if groupMessages.length === 0}
            <div class="no-messages">
              <p>{$_("messaging.no_group_messages")}</p>
            </div>
          {:else}
            {#each groupMessages as message (message.id)}
              <div class="message group-message" class:own={message.isOwn}>
                {#if !message.isOwn}
                  {@const senderUser = users.find(
                    (u) => u.shortname === message.senderId
                  )}
                  <div class="message-sender-info">
                    <div class="sender-avatar tiny">
                      {#if senderUser?.avatar}
                        <img loading="lazy" decoding="async"
                          src={senderUser.avatar}
                          alt={getUserDisplayName(message.senderId)}
                        />
                      {:else}
                        <div class="avatar-placeholder">
                          {getUserDisplayName(message.senderId)
                            .charAt(0)
                            .toUpperCase()}
                        </div>
                      {/if}
                    </div>
                    <div class="sender-details">
                      <div class="message-sender">
                        {getUserDisplayName(message.senderId)}
                      </div>
                      <div class="message-timestamp">
                        {formatTime(message.timestamp)}
                      </div>
                    </div>
                  </div>
                {/if}
                <div class="message-content" class:own-content={message.isOwn}>
                  {#if message.content && message.content !== "attachment"}
                    <div class="message-text">{message.content}</div>
                  {/if}

                  {#if message.isUploading}
                    <div class="upload-status">
                      <div class="upload-spinner"></div>
                      <span>Uploading...</span>
                    </div>
                  {:else if message.uploadFailed}
                    <div class="upload-failed">
                      <span class="error-icon">⚠️</span>
                      <span>{$_("labels.upload_failed")}</span>
                    </div>
                  {/if}

                  {#if storedAttachments(message.attachments).length > 0}
                    <MessengerAttachments
                      attachments={storedAttachments(message.attachments)}
                      resource_type={ResourceType.media}
                      space_name="messages"
                      subpath="/messages"
                      parent_shortname={message.id}
                      isOwner={message.isOwn}
                    />
                  {/if}
                </div>
                {#if message.isOwn}
                  <div class="message-timestamp own-timestamp">
                    {formatTime(message.timestamp)}
                  </div>
                {/if}
              </div>
            {/each}
          {/if}
        </div>

        <MessageInput
          {currentMessage}
          {selectedAttachments}
          {isConnected}
          {isRecording}
          {isAttachmentLoading}
          {recordingDuration}
          placeholder={$_("route_labels.placeholder_type_message_group")}
          onSend={sendGroupMessage}
          onFileSelect={handleFileSelect}
          onKeydown={handleKeydown}
          onStartRecording={startVoiceRecording}
          onStopRecording={stopVoiceRecording}
          onCancelRecording={cancelVoiceRecording}
          onRemoveAttachment={removeAttachment}
          onMessageChange={(value) => (currentMessage = value)}
        />
      {:else}
        <div class="no-chat-selected">
          <div class="no-chat-message">
            <h3>
              {chatMode === "direct"
                ? $_("messaging.select_user_to_chat")
                : "Select a group to chat"}
            </h3>
          </div>
        </div>
      {/if}
    </div>
  </div>
</div>

<!-- Group Modals -->
<GroupModal
  mode="create"
  show={showGroupForm}
  onClose={() => (showGroupForm = false)}
  groupName={newGroupName}
  groupDescription={newGroupDescription}
  participants={selectedGroupParticipants.map((p) => p.shortname)}
  availableUsers={users.filter((u) => u.isActive)}
  onSave={createNewGroup}
  onNameChange={(value) => (newGroupName = value)}
  onDescriptionChange={(value) => (newGroupDescription = value)}
  onAddParticipant={(user) => {
    if (
      !selectedGroupParticipants.some((p) => p.shortname === user.shortname)
    ) {
      selectedGroupParticipants = [...selectedGroupParticipants, user];
    }
  }}
  onRemoveParticipant={(userShortname) => {
    selectedGroupParticipants = selectedGroupParticipants.filter(
      (p) => p.shortname !== userShortname
    );
  }}
  {getUserDisplayName}
/>

<GroupModal
  mode="edit"
  show={showGroupEditForm}
  onClose={() => (showGroupEditForm = false)}
  groupName={editGroupName}
  groupDescription={editGroupDescription}
  participants={editGroupParticipants}
  availableUsers={availableUsersForGroup}
  onSave={updateGroupDetails}
  onAddParticipant={addParticipantToGroup}
  onRemoveParticipant={removeParticipantFromGroup}
  onNameChange={(value) => (editGroupName = value)}
  onDescriptionChange={(value) => (editGroupDescription = value)}
  {getUserDisplayName}
/>

<style>
  .chat-container {
    height: 100vh;
    display: flex;
    flex-direction: column;
    background: var(--color-surface);
  }

  .chat-content {
    flex: 1;
    display: flex;
    min-height: 0;
  }

  /* Default LTR Layout */
  .users-sidebar {
    width: 320px;
    background: var(--color-surface);
    border-inline-end: 1px solid var(--color-border);
    display: flex;
    flex-direction: column;
    order: 1;
  }

  .chat-area {
    flex: 1;
    display: flex;
    flex-direction: column;
    background: var(--color-surface);
    order: 2;
  }

  /* RTL Layout */


  /* Messages and Chat Area Styles */

  .chat-user-header {
    padding: 1rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

  .chat-user-info {
    display: flex;
    align-items: center;
  }

  .chat-user-info > div:last-child {
    margin-inline-start: 0.75rem;
  }


  .chat-user-name {
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 0.125rem;
  }

  .chat-user-status {
    font-size: 0.875rem;
  }

  .messages-container {
    flex: 1;
    overflow-y: auto;
    padding: 1rem;
    background: var(--color-surface);
  }

  .loading-older-messages {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    padding: 1rem;
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }

  .loading-older-messages .loading-spinner {
    width: 16px;
    height: 16px;
    border: 2px solid var(--color-border);
    border-radius: 50%;
    border-top-color: var(--color-text-muted);
    animation: spin 1s ease-in-out infinite;
  }

  .message {
    display: flex;
    margin-bottom: 1rem;
  }

  /* Group Message Enhancements */
  .message.group-message {
    flex-direction: column;
    gap: 0.5rem;
  }

  .message.group-message.own {
    align-items: flex-end;
  }

  .message.group-message:not(.own) {
    align-items: flex-start;
  }

  .message-sender-info {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.25rem;
  }

  .sender-avatar.tiny {
    width: 24px;
    height: 24px;
    border-radius: 50%;
    overflow: hidden;
    flex-shrink: 0;
  }

  .sender-avatar.tiny img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }

  .sender-avatar.tiny .avatar-placeholder {
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--color-border);
    color: var(--color-text-muted);
    font-size: 0.6rem;
    font-weight: 600;
  }

  .sender-details {
    display: flex;
    flex-direction: column;
    gap: 0.125rem;
  }

  .message-sender {
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-text-muted);
    margin: 0;
  }

  .message-content.own-content {
    background: var(--color-primary);
    color: white;
    border-end-end-radius: 4px;
  }

  .message.group-message .message-timestamp {
    font-size: 0.625rem;
    color: var(--color-text-faint);
    margin: 0;
  }

  .own-timestamp {
    align-self: flex-end;
    text-align: end;
    margin-top: 0.25rem;
  }

  /* RTL adjustments for group messages */





  /* Default LTR message alignment */
  .message.own {
    justify-content: flex-end;
  }

  .message:not(.own) {
    justify-content: flex-start;
  }

  /* RTL message alignment */


  .message-content {
    max-width: 70%;
    background: var(--color-surface);
    padding: 0.75rem 1rem;
    border-radius: 1rem;
    box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
  }

  .message.own .message-content {
    background: var(--color-info);
    color: white;
  }

  .message-text {
    word-wrap: break-word;
    line-height: 1.4;
  }

  .message-time {
    font-size: 0.75rem;
    opacity: 0.7;
    margin-top: 0.25rem;
  }

  @keyframes pulse-recording {
    0%,
    100% {
      opacity: 1;
      transform: scale(1);
    }
    50% {
      opacity: 0.5;
      transform: scale(1.2);
    }
  }

  .file-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .file-size {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  .voice-message-preview {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem;
    background: var(--color-info-soft);
    border: 1px solid var(--color-info-soft);
    border-radius: 0.5rem;
    max-width: 280px;
  }

  .voice-message-icon {
    width: 40px;
    height: 40px;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.5rem;
    background: var(--color-info-soft);
    border-radius: 0.25rem;
    color: var(--color-primary);
  }

  .voice-message-info {
    flex: 1;
    min-width: 0;
  }

  .voice-message-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-primary);
    margin-bottom: 0.125rem;
  }

  .voice-audio-control {
    width: 200px;
    height: 30px;
  }

  .voice-audio-control::-webkit-media-controls-panel {
    background-color: transparent;
  }

  /* Message Attachments */
  .message-attachments {
    margin-top: 0.5rem;
  }

  .message-attachments :global(.attachments-container) {
    width: 100%;
    max-width: 100%;
  }

  .message-attachments :global(.attachment-card) {
    background: transparent;
    border: none;
    border-radius: 8px;
    box-shadow: none;
    margin-bottom: 0.5rem;
    max-width: 100%;
    overflow: hidden;
  }

  .message-attachments :global(.attachment-card:hover) {
    transform: none;
    box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);
  }

  .message-attachments :global(.attachment-header) {
    display: none;
  }

  .message-attachments :global(.attachment-preview) {
    height: auto;
    min-height: auto;
    background: transparent;
    border-radius: 8px;
    overflow: hidden;
  }

  .message-attachments :global(.media-wrapper) {
    height: auto;
    max-height: 200px;
  }

  .message-attachments :global(.attachment-preview img) {
    width: 100%;
    height: auto;
    max-height: 200px;
    object-fit: cover;
    border-radius: 8px;
    border: 1px solid var(--color-border);
  }

  .message-attachments :global(.attachment-preview video) {
    width: 100%;
    height: auto;
    max-height: 200px;
    max-width: 280px;
    border-radius: 8px;
    border: 1px solid var(--color-border);
  }

  .message-attachments :global(.media-overlay) {
    border-radius: 8px;
  }

  .message-attachments :global(.attachment-info) {
    padding: 0.5rem 0 0 0;
    background: transparent;
  }

  .message-attachments :global(.attachment-name) {
    font-size: 0.75rem;
    color: currentColor;
    opacity: 0.8;
    margin-bottom: 0;
  }

  .message-attachments :global(.unsupported-file) {
    height: 80px;
    background: rgba(248, 250, 252, 0.5);
    border: 1px solid var(--color-border);
    border-radius: 8px;
  }

  .attachment-item {
    margin-bottom: 0.5rem;
  }

  .attachment-item:last-child {
    margin-bottom: 0;
  }

  .attachment-image {
    max-width: 200px;
    max-height: 200px;
    object-fit: cover;
    border-radius: 0.5rem;
    border: 1px solid var(--color-border);
  }

  .attachment-file {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    max-width: 250px;
  }


  .file-icon-display {
    font-size: 1.5rem;
    width: 40px;
    height: 40px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--color-surface);
    border-radius: 0.25rem;
  }

  .file-details {
    flex: 1;
    min-width: 0;
  }


  .temp-attachment {
    opacity: 0.7;
  }

  .loading-spinner {
    width: 16px;
    height: 16px;
    border: 2px solid #ffffff40;
    border-radius: 50%;
    border-top-color: var(--color-surface-2);
    animation: spin 1s ease-in-out infinite;
  }

  .no-chat-selected {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--color-surface);
  }

  .no-chat-message {
    text-align: center;
    color: var(--color-text-muted);
  }

  .no-chat-message h3 {
    color: var(--color-text);
    margin-bottom: 0.5rem;
  }

  .loading {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 2rem;
    color: var(--color-text-muted);
  }

  .upload-status {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem;
    background: rgba(59, 130, 246, 0.1);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    color: var(--color-primary);
    margin-bottom: 0.5rem;
  }

  .upload-spinner {
    width: 16px;
    height: 16px;
    border: 2px solid #3b82f640;
    border-radius: 50%;
    border-top-color: var(--color-primary);
    animation: spin 1s ease-in-out infinite;
  }

  .upload-failed {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem;
    background: rgba(239, 68, 68, 0.1);
    border-radius: 0.5rem;
    font-size: 0.875rem;
    color: var(--color-danger);
    margin-bottom: 0.5rem;
  }

  .error-icon {
    font-size: 1rem;
  }

  .message.own .upload-status {
    background: rgba(255, 255, 255, 0.2);
    color: rgba(255, 255, 255, 0.9);
  }

  .message.own .upload-spinner {
    border-color: rgba(255, 255, 255, 0.3);
    border-top-color: white;
  }

  .message.own .upload-failed {
    background: rgba(255, 255, 255, 0.2);
    color: rgba(255, 255, 255, 0.9);
  }

  .chat-group-header {
    display: flex;
    align-items: center;
    padding: 1rem;
    border-bottom: 1px solid var(--color-border);
    background: var(--color-surface);
    justify-content: space-between;
  }

  .chat-group-info {
    display: flex;
    align-items: center;
  }

  .group-avatar.small {
    width: 32px;
    height: 32px;
    margin-inline-end: 0.75rem;
  }

  .chat-group-name {
    font-weight: 600;
    color: var(--color-text);
    margin-bottom: 0.125rem;
  }

  .chat-group-status {
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .group-participants-preview {
    font-size: 0.75rem;
    color: var(--color-text-faint);
    margin-top: 0.25rem;
    font-style: italic;
  }

  .message-sender {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    margin-bottom: 0.25rem;
    font-weight: 500;
  }

  @media (max-width: 768px) {
    .users-sidebar {
      width: 280px;
    }

    .message-content {
      max-width: 85%;
    }

    .chat-content {
      flex-direction: column;
    }

    .users-sidebar {
      width: 100%;
      height: 40vh;
      order: 1;
    }

    .chat-area {
      order: 2;
      height: 60vh;
    }

  }

  /* Group Edit Styles */
  .group-header-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .edit-group-btn {
    background: transparent;
    border: none;
    padding: 0.5rem;
    border-radius: 50%;
    cursor: pointer;
    font-size: 1.2rem;
    display: flex;
    align-items: center;
    justify-content: center;
    transition: background-color 0.2s;
  }

  .edit-group-btn:hover {
    background: rgba(0, 0, 0, 0.1);
  }
</style>
