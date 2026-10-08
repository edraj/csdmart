<script lang="ts">
  import { _ } from "@/i18n";

  interface Props {
    isConnected: boolean;
    connectionStatus: string;
  }

  let { isConnected, connectionStatus }: Props = $props();
</script>

<div class="chat-header">
  <h1>{$_("messaging.title")}</h1>
  <div
    class="connection-status"
    class:connected={isConnected}
    class:disconnected={!isConnected}
  >
    <div class="status-indicator"></div>
    <span>{connectionStatus}</span>
  </div>
</div>

<style>
  .chat-header {
    background: var(--color-surface-2);
    border-bottom: 1px solid var(--color-border);
    padding: 1rem 1.5rem;
    display: flex;
    justify-content: space-between;
    align-items: center;
    box-shadow: var(--shadow-card);
  }

  .chat-header h1 {
    margin: 0;
    color: var(--color-text);
    font-size: 1.5rem;
  }

  .connection-status {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 1rem;
    border-radius: var(--radius-full);
    font-size: 0.875rem;
    font-weight: 500;
  }

  .connection-status.connected {
    background: var(--color-success-soft);
    color: var(--color-success);
  }

  .connection-status.disconnected {
    background: var(--color-danger-soft);
    color: var(--color-danger);
  }

  .status-indicator {
    width: 8px;
    height: 8px;
    border-radius: var(--radius-full);
    animation: pulse-soft 2s infinite;
  }

  .connected .status-indicator {
    background: var(--color-success);
  }

  .disconnected .status-indicator {
    background: var(--color-danger);
  }
</style>
