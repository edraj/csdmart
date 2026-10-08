<script lang="ts">
  import { _ } from "@/i18n";

  interface Props {
    chatMode: string;
    usersCount: number;
    groupsCount: number;
    onModeChange: (mode: string) => void;
  }

  let { chatMode, usersCount, groupsCount, onModeChange }: Props = $props();
</script>

<div class="chat-mode-tabs">
  <button
    class="tab-btn"
    class:active={chatMode === "direct"}
    onclick={() => onModeChange("direct")}
  >
    💬 {$_("messaging.direct_messages")} ({usersCount})
  </button>
  <button
    class="tab-btn"
    class:active={chatMode === "group"}
    onclick={() => onModeChange("group")}
  >
    👥 {$_("messaging.groups")} ({groupsCount})
  </button>
</div>

<style>
  .chat-mode-tabs {
    display: flex;
    border-bottom: 1px solid var(--color-border);
    background: white;
  }

  .tab-btn {
    flex: 1;
    background: transparent;
    border: none;
    padding: 1rem;
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
    cursor: pointer;
    border-bottom: 2px solid transparent;
    transition: all 0.2s;
  }

  .tab-btn:hover {
    color: var(--color-text);
    background: var(--color-surface);
  }

  .tab-btn.active {
    color: var(--color-primary);
    border-bottom-color: var(--color-primary);
    background: var(--color-surface);
  }
</style>
