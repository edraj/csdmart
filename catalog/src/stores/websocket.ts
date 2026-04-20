/**
 * Global WebSocket store for app-wide real-time connectivity.
 *
 * The WebSocket connects once on authentication and stays connected
 * across page navigations. Pages register listeners for messages
 * instead of managing their own connections.
 *
 * csdmart allows ONE channel subscription per user — the global
 * subscription covers the user's personal space (notifications + messages).
 */

import { writable, get } from "svelte/store";
import {
  getWebSocketService,
  resetWebSocketService,
  type ConnectionStatus,
  type WebSocketMessage,
} from "@/lib/services/websocket";
import { newNotificationType } from "@/stores/newNotificationType";

export const wsConnected = writable(false);
export const wsStatus = writable<ConnectionStatus>("disconnected");

let initialized = false;

/**
 * Initialize the global WebSocket connection.
 * Call once after authentication is confirmed.
 * Subscribes to the user's personal space for notifications and messages.
 *
 * @param token - JWT auth token
 * @param shortname - User's shortname for channel subscription
 */
export async function initGlobalWebSocket(
  token: string,
  shortname?: string,
): Promise<boolean> {
  if (initialized) return get(wsConnected);

  const ws = getWebSocketService(token, {
    onStatusChange: (status) => {
      wsConnected.set(status === "connected");
      wsStatus.set(status);
    },
    onMessage: (data) => {
      handleGlobalMessage(data);
    },
  });

  if (!ws) return false;

  initialized = true;
  const connected = await ws.connect();

  if (connected && shortname) {
    // Subscribe to user's personal space — catches notifications and messages
    // via csdmart's subpath prefix matching
    await ws.subscribe("personal", `/people/${shortname}`);
  }

  return connected;
}

/**
 * Disconnect and clean up the global WebSocket.
 * Call on signout.
 */
export function teardownGlobalWebSocket(): void {
  resetWebSocketService();
  wsConnected.set(false);
  wsStatus.set("disconnected");
  initialized = false;
}

/**
 * Handle messages at the global level (notification badge updates).
 * Individual pages add their own listeners via ws.addMessageListener().
 */
function handleGlobalMessage(data: WebSocketMessage): void {
  if (data.type === "connection_response") return;

  // Plugin broadcast: a CRUD event happened in a subscribed channel
  if (data.type === "notification_subscription" && data.message?.action_type) {
    const action = data.message.action_type;

    if (action === "create" || action === "update") {
      newNotificationType.set(`${action}_event`);
    }
  }
}
