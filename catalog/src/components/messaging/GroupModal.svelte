<script lang="ts">
  import { _ } from "@/i18n";
  import type { UserData } from "@/lib/utils/messagingUtils";
  import Modal from "@/components/Modal.svelte";
  import { UsersSolid } from "flowbite-svelte-icons";

  interface Props {
    mode: "create" | "edit";
    show: boolean;
    groupName: string;
    groupDescription: string;
    participants: string[];
    availableUsers: UserData[];
    currentUserShortname?: string;
    onClose: () => void;
    onSave: () => void;
    onNameChange: (value: string) => void;
    onDescriptionChange: (value: string) => void;
    onAddParticipant: (user: UserData) => void;
    onRemoveParticipant: (shortname: string) => void;
    getUserDisplayName: (shortname: string) => string;
  }

  let {
    mode,
    show,
    groupName,
    groupDescription,
    participants,
    availableUsers,
    currentUserShortname,
    onClose,
    onSave,
    onNameChange,
    onDescriptionChange,
    onAddParticipant,
    onRemoveParticipant,
    getUserDisplayName,
  }: Props = $props();

  let isCreateMode = $state(false);
  let modalTitle = $state("");
  let saveButtonText = $state("");
  let canSave = $state(false);

  $effect(() => {
    isCreateMode = mode === "create";
    modalTitle = isCreateMode
      ? $_("messaging.create_new_group")
      : $_("messaging.edit_group_settings");
    saveButtonText = isCreateMode
      ? $_("messaging.create_group")
      : $_("messaging.update_group");
    canSave =
      !!(groupName.trim() && (isCreateMode ? participants.length > 0 : true));
  });
</script>

{#if show}
  <Modal
    onClose={onClose}
    title={modalTitle}
    ariaLabel={modalTitle}
    size="lg"
  >
    {#snippet icon()}
      <UsersSolid class="w-6 h-6" />
    {/snippet}

    <div class="modal-body-inner">
        <div class="form-group">
          <label for="group-name">{$_("messaging.group_name")}</label>
          <input
            id="group-name"
            type="text"
            value={groupName}
            oninput={(e: any) => onNameChange((e.target as HTMLInputElement).value)}
            placeholder={$_("messaging.group_name_placeholder")}
            maxlength="50"
          />
        </div>

        <div class="form-group">
          <label for="group-description"
            >{$_("messaging.group_description")}</label
          >
          <textarea
            id="group-description"
            value={groupDescription}
            oninput={(e: any) => onDescriptionChange((e.target as HTMLTextAreaElement).value)}
            placeholder={$_("messaging.group_description_placeholder")}
            maxlength="200"
            rows="3"
          ></textarea>
        </div>

        {#if !isCreateMode && participants.length > 0}
          <div class="form-group">
            <h4>
              {$_("messaging.current_participants")} ({participants.length})
            </h4>
            <div class="participants-list">
              {#each participants as participantId (participantId)}
                {#if participantId !== currentUserShortname}
                  <div class="participant-item">
                    <span class="participant-name"
                      >{getUserDisplayName(participantId)}</span
                    >
                    <button
                      class="remove-participant-btn"
                      onclick={() => onRemoveParticipant(participantId)}
                      aria-label={$_("messaging.remove_from_group")}
                      title={$_("messaging.remove_from_group")}
                    >
                      ✕
                    </button>
                  </div>
                {:else}
                  <div class="participant-item current-user">
                    <span class="participant-name"
                      >{getUserDisplayName(participantId)} ({$_(
                        "messaging.you"
                      )})</span
                    >
                    <span class="admin-badge">{$_("messaging.admin")}</span>
                  </div>
                {/if}
              {/each}
            </div>
          </div>
        {/if}

        <div class="form-group">
          <h4>
            {isCreateMode
              ? $_("messaging.select_participants")
              : $_("messaging.add_new_participants")}
          </h4>
          {#if availableUsers.length > 0}
            <div class="available-users-list">
              {#each availableUsers as user (user.shortname)}
                <div class="available-user-item">
                  <span class="user-name">{user.name}</span>
                  <button
                    class="add-user-btn"
                    onclick={() => onAddParticipant(user)}
                    aria-label={$_("messaging.add_to_group")}
                    title={$_("messaging.add_to_group")}
                  >
                    ➕
                  </button>
                </div>
              {/each}
            </div>
          {:else}
            <p class="no-users-message">
              {isCreateMode
                ? $_("messaging.no_users_available")
                : $_("messaging.no_additional_users")}
            </p>
          {/if}
        </div>

        {#if isCreateMode && participants.length > 0}
          <div class="form-group">
            <h4>
              {$_("messaging.selected_participants")} ({participants.length})
            </h4>
            <div class="selected-participants">
              {#each participants as participantId (participantId)}
                <div class="selected-participant">
                  <span>{getUserDisplayName(participantId)}</span>
                  <button
                    class="remove-btn"
                    onclick={() => onRemoveParticipant(participantId)}
                    aria-label={$_("messaging.remove_from_group")}
                  >
                    ✕
                  </button>
                </div>
              {/each}
            </div>
          </div>
        {/if}
    </div>

    {#snippet footer()}
      <button class="cancel-btn" onclick={onClose}>
        {$_("common.cancel")}
      </button>
      <button class="save-btn" onclick={onSave} disabled={!canSave}>
        {saveButtonText}
      </button>
    {/snippet}
  </Modal>
{/if}

<style>
  .modal-body-inner {
    display: flex;
    flex-direction: column;
  }

  .form-group {
    margin-bottom: 1.5rem;
  }

  .form-group h4 {
    margin: 0 0 0.5rem 0;
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-text);
  }

  label {
    display: block;
    margin-bottom: 0.5rem;
    font-weight: 500;
    color: var(--color-text);
  }

  input,
  textarea {
    width: 100%;
    padding: 0.75rem;
    border: 1px solid var(--color-border-strong);
    border-radius: 0.5rem;
    font-size: 1rem;
    transition: border-color 0.2s;
  }

  input:focus,
  textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .participants-list,
  .available-users-list {
    max-height: 200px;
    overflow-y: auto;
    border: 1px solid var(--color-border);
    border-radius: 0.5rem;
    padding: 0.5rem;
  }

  .participant-item,
  .available-user-item {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.5rem;
    border-radius: 0.25rem;
    margin-bottom: 0.25rem;
  }

  .participant-item:hover,
  .available-user-item:hover {
    background: var(--color-surface);
  }

  .participant-item.current-user {
    background: var(--color-info-soft);
    border: 1px solid var(--color-info-soft);
  }

  .participant-name,
  .user-name {
    font-weight: 500;
    color: var(--color-text);
  }

  .admin-badge {
    background: var(--color-success);
    color: white;
    padding: 0.125rem 0.5rem;
    border-radius: 1rem;
    font-size: 0.75rem;
    font-weight: 500;
  }

  .remove-participant-btn {
    background: var(--color-danger);
    color: white;
    border: none;
    padding: 0.25rem 0.5rem;
    border-radius: 0.25rem;
    cursor: pointer;
    font-size: 0.875rem;
    transition: background-color 0.2s;
  }

  .remove-participant-btn:hover {
    background: var(--color-danger);
  }

  .add-user-btn {
    background: var(--color-success);
    color: white;
    border: none;
    padding: 0.25rem 0.5rem;
    border-radius: 0.25rem;
    cursor: pointer;
    font-size: 0.875rem;
    transition: background-color 0.2s;
  }

  .add-user-btn:hover {
    background: var(--color-success);
  }

  .selected-participants {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .selected-participant {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: var(--color-info-soft);
    padding: 0.25rem 0.5rem;
    border-radius: 1rem;
    font-size: 0.875rem;
  }

  .remove-btn {
    background: var(--color-danger);
    color: white;
    border: none;
    border-radius: 50%;
    width: 20px;
    height: 20px;
    font-size: 0.75rem;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .no-users-message {
    color: var(--color-text-muted);
    font-style: italic;
    text-align: center;
    padding: 1rem;
  }

  .cancel-btn,
  .save-btn {
    padding: 0.625rem 1.5rem;
    border-radius: 0.75rem;
    font-weight: 600;
    font-size: 0.875rem;
    cursor: pointer;
    transition: background-color 0.2s;
  }

  .cancel-btn {
    background: transparent;
    border: 1px solid transparent;
    color: var(--color-text-muted);
  }

  .cancel-btn:hover {
    background: var(--color-surface-3);
    color: var(--color-text);
  }

  .save-btn {
    background: var(--color-primary);
    color: white;
    border: none;
    box-shadow: 0 2px 8px rgba(79, 70, 229, 0.2);
  }

  .save-btn:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }

  .save-btn:disabled {
    background: var(--color-text-faint);
    box-shadow: none;
    cursor: not-allowed;
  }
</style>
