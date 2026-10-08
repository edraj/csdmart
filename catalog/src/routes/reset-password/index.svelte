<script lang="ts">
  import { onMount } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { _ } from "@/i18n";
  import { EnvelopeSolid, LockSolid } from "flowbite-svelte-icons";
  import {
    clearResetTarget,
    consumeResetStartOver,
    detectIdentifier,
    markResetCodeIssued,
    requestPasswordReset,
    setResetTarget,
  } from "@/lib/dmart_services/password_reset";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let rawIdentifier = $state("");
  let isSubmitting = $state(false);
  let fieldError = $state("");
  let formError = $state("");
  let startOver = $state(false);

  onMount(() => {
    startOver = consumeResetStartOver();
  });

  $effect(() => setTitle($_("ResetPassword")));

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (isSubmitting) return;
    fieldError = "";
    formError = "";
    // Drop any target left over from an earlier attempt: if this request fails
    // we must not leave step 2 announcing a stale address.
    clearResetTarget();

    const id = detectIdentifier(rawIdentifier);
    if (!id) {
      fieldError = $_("InvalidEmailOrPhone");
      return;
    }

    isSubmitting = true;
    try {
      // A 2xx tells us nothing about whether the account exists — the endpoint
      // answers identically for unknown users and for the resend cooldown. So
      // advance unconditionally and let step 2 be where a typo surfaces.
      await requestPasswordReset(id);
      setResetTarget(id);
      // Stamp the issue time so step 2's resend countdown reflects the
      // server's cooldown rather than restarting on every mount.
      markResetCodeIssued();
      goto("/reset-password/confirm");
    } catch {
      // Transport failure or the auth-by-ip rate limiter.
      formError = $_("ResetFailed");
    } finally {
      isSubmitting = false;
    }
  }
</script>

<div class="auth-container">
  <div class="auth-content">
    <div class="auth-header">
      <div class="icon-wrapper" aria-hidden="true"><LockSolid class="w-6 h-6" /></div>
      <h1 class="auth-title">{$_("ResetPassword")}</h1>
      <p class="auth-description">{$_("ResetPasswordIntro")}</p>
    </div>

    {#if startOver}
      <div class="notice-message" role="status">{$_("ResetStartOver")}</div>
    {/if}

    {#if formError}
      <div class="error-message" role="alert">{formError}</div>
    {/if}

    <form onsubmit={handleSubmit} class="auth-form" novalidate>
      <div class="form-group">
        <label for="identifier" class="form-label">
          <EnvelopeSolid class="label-icon" aria-hidden="true" />
          {$_("EmailOrPhone")}
        </label>
        <input
          id="identifier"
          type="text"
          bind:value={rawIdentifier}
          placeholder={$_("EmailOrPhone")}
          class="form-input"
          class:error={fieldError}
          disabled={isSubmitting}
          autocomplete="username"
          aria-invalid={!!fieldError}
          aria-describedby={fieldError ? "identifier-error" : undefined}
        />
        {#if fieldError}
          <p id="identifier-error" class="error-text-small" role="alert">
            {fieldError}
          </p>
        {/if}
      </div>

      <button type="submit" class="submit-button" disabled={isSubmitting} aria-busy={isSubmitting}>
        {#if isSubmitting}
          <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
        {/if}
        {$_("SendResetCode")}
      </button>
    </form>

    <div class="back-link">
      <a class="link-button" href={withBase("/login")}>{$_("BackToLogin")}</a>
    </div>
  </div>
</div>

<style>
  .auth-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--gradient-page);
    padding: 2rem var(--space-page-x);
  }
  .auth-content {
    width: 100%;
    max-width: 28rem;
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-modal);
    box-shadow: var(--shadow-card);
    padding: 2rem;
  }
  .auth-header {
    text-align: center;
    margin-bottom: 1.5rem;
  }
  .icon-wrapper {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 3rem;
    height: 3rem;
    border-radius: var(--radius-full);
    background: var(--color-primary);
    color: var(--color-text-on-primary);
    margin-bottom: 0.75rem;
  }
  .auth-title {
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--color-text);
  }
  .auth-description {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin-top: 0.375rem;
  }
  .auth-form {
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .form-group {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
  }
  .form-label {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .form-label :global(.label-icon) {
    width: 0.875rem;
    height: 0.875rem;
    color: var(--color-text-faint);
  }
  .form-input {
    width: 100%;
    padding: 0.625rem 0.75rem;
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface);
    color: var(--color-text);
  }
  .form-input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
  }
  .form-input.error {
    border-color: var(--color-danger);
  }
  .error-text-small {
    font-size: 0.8125rem;
    color: var(--color-danger);
  }
  .error-message {
    padding: 0.75rem 1rem;
    border-radius: var(--radius-card);
    background: var(--color-danger-bg);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-fg);
    font-size: 0.875rem;
    margin-bottom: 1rem;
  }
  /* "Your reset session ended, start again" is information, not a failure —
     giving it the red error treatment reads as though something broke. */
  .notice-message {
    padding: 0.75rem 1rem;
    border-radius: var(--radius-card);
    background: var(--color-warning-bg);
    border: 1px solid var(--color-warning-border);
    color: var(--color-warning-fg);
    font-size: 0.875rem;
    margin-bottom: 1rem;
  }
  .submit-button {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    width: 100%;
    padding: 0.6875rem;
    border: none;
    border-radius: var(--radius-control);
    background: var(--color-primary);
    color: var(--color-text-on-primary);
    font-weight: 600;
    cursor: pointer;
  }
  .submit-button:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .submit-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
  .back-link {
    text-align: center;
    margin-top: 1.25rem;
  }
  .link-button {
    background: none;
    border: none;
    color: var(--color-primary);
    font-weight: 600;
    cursor: pointer;
    text-decoration: none;
    font-size: 0.875rem;
  }
  .link-button:hover {
    text-decoration: underline;
  }
</style>
