<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "@roxi/routify";
  import { _, locale } from "@/i18n";
  import { EnvelopeSolid, LockSolid } from "flowbite-svelte-icons";
  import { detectIdentifier, requestPasswordReset } from "@/lib/dmart_services";
  import {
    clearResetTarget,
    consumeResetStartOver,
    setResetTarget,
  } from "@/lib/reset_target";

  $goto;

  let rawIdentifier = $state("");
  let isSubmitting = $state(false);
  let fieldError = $state("");
  let formError = $state("");
  let startOver = $state(false);

  const isRTL = $derived($locale === "ar" || $locale === "ku");

  onMount(() => {
    startOver = consumeResetStartOver();
  });

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
      $goto("/reset-password/confirm");
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
      <div class="icon-wrapper"><LockSolid class="text-white w-6 h-6" /></div>
      <h1 class="auth-title">{$_("ResetPassword")}</h1>
      <p class="auth-description">{$_("ResetPasswordIntro")}</p>
    </div>

    {#if startOver}
      <div class="error-message" class:rtl={isRTL} role="status">{$_("ResetStartOver")}</div>
    {/if}

    {#if formError}
      <div class="error-message" class:rtl={isRTL} role="alert">{formError}</div>
    {/if}

    <form onsubmit={handleSubmit} class="auth-form">
      <div class="form-group">
        <label for="identifier" class="form-label" class:rtl={isRTL}>
          <EnvelopeSolid class="label-icon" />
          {$_("EmailOrPhone")}
        </label>
        <input
          id="identifier"
          type="text"
          bind:value={rawIdentifier}
          placeholder={$_("EmailOrPhone")}
          class="form-input"
          class:error={fieldError}
          class:rtl={isRTL}
          disabled={isSubmitting}
          autocomplete="username"
          aria-invalid={!!fieldError}
          aria-describedby={fieldError ? "identifier-error" : undefined}
        />
        {#if fieldError}
          <p id="identifier-error" class="error-text-small" class:rtl={isRTL} role="alert">
            {fieldError}
          </p>
        {/if}
      </div>

      <button type="submit" class="submit-button" class:rtl={isRTL} disabled={isSubmitting}>
        {#if isSubmitting}
          <div class="loading-spinner"></div>
        {/if}
        {$_("SendResetCode")}
      </button>
    </form>

    <div class="back-link" class:rtl={isRTL}>
      <button class="link-button" onclick={() => $goto("/login")}>
        {$_("BackToLogin")}
      </button>
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
    padding: 2rem 1rem;
  }
  .auth-content {
    width: 100%;
    max-width: 28rem;
    background: var(--color-gray-50);
    border-radius: 1rem;
    box-shadow: 0 10px 30px rgba(0, 0, 0, 0.08);
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
    border-radius: 9999px;
    background: var(--color-primary-500);
    margin-bottom: 0.75rem;
  }
  .auth-title {
    font-size: 1.5rem;
    font-weight: 700;
  }
  .auth-description {
    font-size: 0.875rem;
    opacity: 0.75;
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
  }
  .form-label.rtl,
  .error-text-small.rtl,
  .submit-button.rtl,
  .error-message.rtl,
  .back-link.rtl {
    direction: rtl;
  }
  .form-input {
    width: 100%;
    padding: 0.625rem 0.75rem;
    border: 1px solid var(--color-gray-300);
    border-radius: 0.5rem;
    background: transparent;
  }
  .form-input:focus {
    outline: 2px solid var(--color-primary-500);
    outline-offset: 1px;
  }
  .form-input.error {
    border-color: var(--color-error);
  }
  .form-input.rtl {
    direction: rtl;
    text-align: right;
  }
  .error-text-small {
    font-size: 0.8125rem;
    color: var(--color-error);
  }
  .error-message {
    padding: 0.75rem;
    border-radius: 0.5rem;
    background: rgba(220, 38, 38, 0.08);
    color: var(--color-error);
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
    border-radius: 0.5rem;
    background: var(--color-primary-500);
    color: #fff;
    font-weight: 600;
    cursor: pointer;
  }
  .submit-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
  .loading-spinner {
    width: 1rem;
    height: 1rem;
    border: 2px solid rgba(255, 255, 255, 0.4);
    border-top-color: #fff;
    border-radius: 9999px;
    animation: spin 0.7s linear infinite;
  }
  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
  .back-link {
    text-align: center;
    margin-top: 1.25rem;
  }
  .link-button {
    background: none;
    border: none;
    color: var(--color-primary-500);
    font-weight: 600;
    cursor: pointer;
  }
</style>
