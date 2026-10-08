<script lang="ts">
  import { _ } from "@/i18n";
  let {
    content = $bindable({}),
  }: {
    content: any;
  } = $props();

  content = {
    name: content.name || "",
    states: content.states || [],
    illustration: content.illustration || "",
    initial_state: content.initial_state || [],
  };

  let openAccordions: Set<number> = $state(new Set());

  function toggleAccordion(index: number) {
    if (openAccordions.has(index)) {
      openAccordions.delete(index);
    } else {
      openAccordions.add(index);
    }
    openAccordions = new Set(openAccordions);
  }

  function addState() {
    if (content.states === undefined) {
      content.states = [];
    }
    content.states = [
      ...content.states,
      {
        name: "",
        state: "",
        next: [],
        resolutions: [],
      },
    ];
  }

  function removeState(event: Event, index: number) {
    event.stopPropagation();
    content.states = content.states.filter((_: any, i: number) => i !== index);
  }

  function addNextTransition(stateIndex: number) {
    content.states[stateIndex].next = [
      ...content.states[stateIndex].next,
      { roles: [], state: "", action: "" },
    ];
  }

  function removeNextTransition(stateIndex: number, transitionIndex: number) {
    content.states[stateIndex].next = content.states[stateIndex].next.filter(
      (_: any, i: number) => i !== transitionIndex
    );
  }

  function addResolution(stateIndex: number) {
    content.states[stateIndex].resolutions = [
      ...content.states[stateIndex].resolutions,
      { ar: "", en: "", ku: "", key: "" },
    ];
  }

  function removeResolution(stateIndex: number, resolutionIndex: number) {
    content.states[stateIndex].resolutions = content.states[
      stateIndex
    ].resolutions.filter((_: any, i: number) => i !== resolutionIndex);
  }

  function addInitialState() {
    if (content.initial_state === undefined) {
      content.initial_state = [];
    }
    content.initial_state = [...content.initial_state, { name: "", roles: [] }];
  }

  function removeInitialState(index: number) {
    content.initial_state = content.initial_state.filter(
      (_: any, i: number) => i !== index
    );
  }

  function addRole(item: any) {
    if (item.roles === undefined) {
      item.roles = [];
    }
    item.roles = [...(item.roles || []), ""];
  }

  function removeRole(item: any, roleIndex: number) {
    item.roles = item.roles.filter((_: any, i: number) => i !== roleIndex);
  }
</script>

<div class="card main-card">
  <h1 class="title">{$_("workflow_form.title")}</h1>

  <div class="form-group">
    <label for="workflowName" class="label">{$_("workflow_form.name")}</label>
    <input
      id="workflowName"
      class="input"
      bind:value={content.name}
      placeholder={$_("workflow_form.name_placeholder")}
      required
    />
  </div>

  <div class="form-group">
    <label for="illustration" class="label">{$_("workflow_form.illustration")}</label>
    <input
      id="illustration"
      class="input"
      bind:value={content.illustration}
      placeholder={$_("workflow_form.illustration_placeholder")}
    />
  </div>

  <div class="card section-card">
    <h3 class="section-title">{$_("workflow_form.initial_states")}</h3>
    <div class="space-y">
      {#each content.initial_state as initialState, index (index)}
        <div class="card inner-card">
          <div class="card-header">
            <h4 class="card-title">Initial State {index + 1}</h4>
            <button
              class="btn btn-danger btn-sm"
              onclick={() => removeInitialState(index)}
            >
              <svg
                class="icon"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
              >
                <path
                  d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />
              </svg>
              Remove
            </button>
          </div>

          <div class="space-y">
            <div class="form-group">
              <label for={`initialState${index}Name`} class="label">{$_("name")}</label>
              <input
                id={`initialState${index}Name`}
                class="input"
                bind:value={initialState.name}
                placeholder={$_("workflow_form.initial_state_placeholder")}
              />
            </div>

            <div class="form-group">
              <p class="label">{$_("roles")}</p>
              {#each initialState.roles as _role, roleIndex (roleIndex)}
                <div class="input-group">
                  <input
                    class="input"
                    bind:value={initialState.roles[roleIndex]}
                    placeholder={$_("workflow_form.role_placeholder")}
                  />
                  <button
                    class="btn btn-danger btn-icon"
                    aria-label={$_("workflow_form.remove_role")}
                    onclick={() => removeRole(initialState, roleIndex)}
                  >
                    <svg
                      class="icon"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                    >
                      <path
                        d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                        stroke-width="2"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      />
                    </svg>
                  </button>
                </div>
              {/each}
              <button
                class="btn btn-secondary btn-sm"
                onclick={() => addRole(initialState)}
              >
                <svg
                  class="icon"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                >
                  <path
                    d="M12 5v14M5 12h14"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  />
                </svg>
                Add Role
              </button>
            </div>
          </div>
        </div>
      {/each}

      <button onclick={addInitialState} class="btn btn-secondary">
        <svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor">
          <path
            d="M12 5v14M5 12h14"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          />
        </svg>
        Add Initial State
      </button>
    </div>
  </div>

  <div class="card section-card">
    <h3 class="section-title">{$_("workflow_form.states")}</h3>
    <div class="space-y">
      {#each content.states as state, stateIndex (stateIndex)}
        <div class="accordion">
          <div
            class="accordion-header"
            role="button"
            tabindex="0"
            onclick={() => toggleAccordion(stateIndex)}
            onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') toggleAccordion(stateIndex); }}
          >
            <span class="accordion-title"
              >{state.name || `State ${stateIndex + 1}`}</span
            >
            <div class="accordion-actions">
              <button
                class="btn btn-danger btn-icon"
                aria-label={$_("workflow_form.remove_state")}
                onclick={(e) => removeState(e, stateIndex)}
              >
                <svg
                  class="icon"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                >
                  <path
                    d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                    stroke-width="2"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                  />
                </svg>
              </button>
              <svg
                class="icon chevron {openAccordions.has(stateIndex)
                  ? 'open'
                  : ''}"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
              >
                <path
                  d="M19 9l-7 7-7-7"
                  stroke-width="2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />
              </svg>
            </div>
          </div>

          {#if openAccordions.has(stateIndex)}
            <div class="accordion-content">
              <div class="grid-2">
                <div class="form-group">
                  <label for={`state${stateIndex}Name`} class="label"
                    >{$_("name")}</label
                  >
                  <input
                    id={`state${stateIndex}Name`}
                    class="input"
                    bind:value={state.name}
                    placeholder={$_("workflow_form.state_name_placeholder")}
                  />
                </div>
                <div class="form-group">
                  <label for={`state${stateIndex}Id`} class="label"
                    >{$_("workflow_form.state_id")}</label
                  >
                  <input
                    id={`state${stateIndex}Id`}
                    class="input"
                    bind:value={state.state}
                    placeholder={$_("workflow_form.state_id_placeholder")}
                  />
                </div>
              </div>

              <!-- Next Transitions -->
              <div class="subsection">
                <p class="subsection-title">{$_("workflow_form.next_transitions")}</p>
                <div class="space-y">
                  {#each state.next as transition, transitionIndex (transitionIndex)}
                    <div class="card transition-card">
                      <div class="card-header">
                        <h5 class="card-subtitle">
                          Transition {transitionIndex + 1}
                        </h5>
                        <button
                          class="btn btn-danger btn-icon"
                          aria-label={$_("workflow_form.remove_transition")}
                          onclick={() =>
                            removeNextTransition(stateIndex, transitionIndex)}
                        >
                          <svg
                            class="icon"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                          >
                            <path
                              d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                              stroke-width="2"
                              stroke-linecap="round"
                              stroke-linejoin="round"
                            />
                          </svg>
                        </button>
                      </div>

                      <div class="grid-2">
                        <div class="form-group">
                          <label class="label" for="next-state-{stateIndex}-{transitionIndex}">{$_("workflow_form.next_state")}</label>
                          <input
                            id="next-state-{stateIndex}-{transitionIndex}"
                            class="input"
                            bind:value={transition.state}
                            placeholder={$_("workflow_form.next_state_placeholder")}
                          />
                        </div>
                        <div class="form-group">
                          <label class="label" for="action-{stateIndex}-{transitionIndex}">{$_("workflow_form.action")}</label>
                          <input
                            id="action-{stateIndex}-{transitionIndex}"
                            class="input"
                            bind:value={transition.action}
                            placeholder={$_("workflow_form.action_placeholder")}
                          />
                        </div>
                      </div>

                      <div class="form-group">
                        <p class="label">{$_("roles")}</p>
                        {#each transition.roles as _role, roleIndex (roleIndex)}
                          <div class="input-group">
                            <input
                              class="input"
                              bind:value={transition.roles[roleIndex]}
                              placeholder={$_("workflow_form.role_placeholder")}
                            />
                            <button
                              class="btn btn-danger btn-icon"
                              aria-label={$_("workflow_form.remove_role")}
                              onclick={() => removeRole(transition, roleIndex)}
                            >
                              <svg
                                class="icon"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                              >
                                <path
                                  d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                                  stroke-width="2"
                                  stroke-linecap="round"
                                  stroke-linejoin="round"
                                />
                              </svg>
                            </button>
                          </div>
                        {/each}
                        <button
                          class="btn btn-secondary btn-sm"
                          onclick={() => addRole(transition)}
                        >
                          <svg
                            class="icon"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                          >
                            <path
                              d="M12 5v14M5 12h14"
                              stroke-width="2"
                              stroke-linecap="round"
                              stroke-linejoin="round"
                            />
                          </svg>
                          Add Role
                        </button>
                      </div>
                    </div>
                  {/each}
                  <button
                    class="btn btn-secondary"
                    onclick={() => addNextTransition(stateIndex)}
                  >
                    <svg
                      class="icon"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                    >
                      <path
                        d="M12 5v14M5 12h14"
                        stroke-width="2"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      />
                    </svg>
                    Add Transition
                  </button>
                </div>
              </div>

              <!-- Resolutions -->
              <div class="subsection">
                <p class="subsection-title">{$_("workflow_form.resolutions")}</p>
                <div class="space-y">
                  {#each state.resolutions as resolution, resolutionIndex (resolutionIndex)}
                    <div class="card transition-card">
                      <div class="card-header">
                        <h5 class="card-subtitle">
                          Resolution {resolutionIndex + 1}
                        </h5>
                        <button
                          class="btn btn-danger btn-icon"
                          aria-label={$_("workflow_form.remove_resolution")}
                          onclick={() =>
                            removeResolution(stateIndex, resolutionIndex)}
                        >
                          <svg
                            class="icon"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                          >
                            <path
                              d="M3 6h18M19 6v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6m3 0V4a2 2 0 012-2h4a2 2 0 012 2v2"
                              stroke-width="2"
                              stroke-linecap="round"
                              stroke-linejoin="round"
                            />
                          </svg>
                        </button>
                      </div>

                      <div class="grid-2">
                        <div class="form-group">
                          <label class="label" for="res-key-{stateIndex}-{resolutionIndex}">{$_("workflow_form.key")}</label>
                          <input
                            id="res-key-{stateIndex}-{resolutionIndex}"
                            class="input"
                            bind:value={resolution.key}
                            placeholder={$_("workflow_form.resolution_key_placeholder")}
                          />
                        </div>
                        <div class="form-group">
                          <label class="label" for="res-en-{stateIndex}-{resolutionIndex}">{$_("english")}</label>
                          <input
                            id="res-en-{stateIndex}-{resolutionIndex}"
                            class="input"
                            bind:value={resolution.en}
                            placeholder={$_("labels.translation_lang", { values: { language: $_("english") } })}
                          />
                        </div>
                        <div class="form-group">
                          <label class="label" for="res-ar-{stateIndex}-{resolutionIndex}">{$_("arabic")}</label>
                          <input
                            id="res-ar-{stateIndex}-{resolutionIndex}"
                            class="input"
                            bind:value={resolution.ar}
                            placeholder={$_("labels.translation_lang", { values: { language: $_("arabic") } })}
                          />
                        </div>
                        <div class="form-group">
                          <label class="label" for="res-ku-{stateIndex}-{resolutionIndex}">{$_("kurdish")}</label>
                          <input
                            id="res-ku-{stateIndex}-{resolutionIndex}"
                            class="input"
                            bind:value={resolution.ku}
                            placeholder={$_("labels.translation_lang", { values: { language: $_("kurdish") } })}
                          />
                        </div>
                      </div>
                    </div>
                  {/each}
                  <button
                    class="btn btn-secondary"
                    onclick={() => addResolution(stateIndex)}
                  >
                    <svg
                      class="icon"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                    >
                      <path
                        d="M12 5v14M5 12h14"
                        stroke-width="2"
                        stroke-linecap="round"
                        stroke-linejoin="round"
                      />
                    </svg>
                    Add Resolution
                  </button>
                </div>
              </div>
            </div>
          {/if}
        </div>
      {/each}

      <button onclick={addState} class="btn btn-secondary">
        <svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor">
          <path
            d="M12 5v14M5 12h14"
            stroke-width="2"
            stroke-linecap="round"
            stroke-linejoin="round"
          />
        </svg>
        Add State
      </button>
    </div>
  </div>
</div>

<style>
  * {
    box-sizing: border-box;
  }

  .card {
    background: white;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 16px;
  }

  .main-card {
    margin: 16px 16px;
  }

  .section-card {
    margin-top: 16px;
  }

  .inner-card {
    padding: 16px;
  }

  .transition-card {
    padding: 12px;
  }

  .title {
    font-size: 24px;
    font-weight: bold;
    margin: 0 0 16px 0;
    color: var(--color-text);
  }

  .section-title {
    font-size: 20px;
    font-weight: 600;
    margin: 0 0 12px 0;
    color: var(--color-text);
  }

  .subsection-title {
    font-size: 18px;
    font-weight: 500;
    margin: 16px 0 8px 0;
    color: var(--color-text);
    display: block;
  }

  .card-title {
    font-size: 18px;
    font-weight: 500;
    margin: 0;
    color: var(--color-text);
  }

  .card-subtitle {
    font-size: 14px;
    font-weight: 500;
    margin: 0;
    color: var(--color-text);
  }

  .card-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 12px;
  }

  .form-group {
    margin-bottom: 16px;
  }

  .label {
    display: block;
    margin-bottom: 6px;
    font-size: 14px;
    font-weight: 500;
    color: var(--color-text);
  }

  .input {
    width: 100%;
    padding: 8px 12px;
    border: 1px solid var(--color-border-strong);
    border-radius: 6px;
    font-size: 14px;
    transition:
      border-color 0.2s,
      box-shadow 0.2s;
  }

  .input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1);
  }

  .input::placeholder {
    color: var(--color-text-faint);
  }

  .input-group {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 8px;
  }

  .input-group .input {
    flex: 1;
  }

  .btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    padding: 8px 16px;
    border: 1px solid transparent;
    border-radius: 6px;
    font-size: 14px;
    font-weight: 500;
    cursor: pointer;
    transition: all 0.2s;
  }

  .btn:hover {
    opacity: 0.9;
  }

  .btn-sm {
    padding: 6px 12px;
    font-size: 13px;
  }

  .btn-icon {
    padding: 6px;
  }

  .btn-secondary {
    background: white;
    border-color: var(--color-primary);
    color: var(--color-primary);
  }

  .btn-secondary:hover {
    background: var(--color-info-soft);
  }

  .btn-danger {
    background: white;
    border-color: var(--color-danger);
    color: var(--color-danger);
  }

  .btn-danger:hover {
    background: var(--color-danger-soft);
  }

  .icon {
    width: 16px;
    height: 16px;
    stroke-width: 2;
  }

  .space-y > * + * {
    margin-top: 16px;
  }

  .grid-2 {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 16px;
  }

  .subsection {
    margin-top: 16px;
    padding-top: 16px;
    border-top: 1px solid var(--color-border);
  }

  .accordion {
    border: 1px solid var(--color-border);
    border-radius: 8px;
    overflow: hidden;
  }

  .accordion-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 12px 16px;
    background: var(--color-surface);
    cursor: pointer;
    transition: background 0.2s;
  }

  .accordion-header:hover {
    background: var(--color-surface-3);
  }

  .accordion-title {
    font-weight: 500;
    color: var(--color-text);
  }

  .accordion-actions {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .accordion-content {
    padding: 16px;
    border-top: 1px solid var(--color-border);
  }

  .chevron {
    transition: transform 0.2s;
  }

  .chevron.open {
    transform: rotate(180deg);
  }

  @media (max-width: 768px) {
    .grid-2 {
      grid-template-columns: 1fr;
    }
  }
</style>
