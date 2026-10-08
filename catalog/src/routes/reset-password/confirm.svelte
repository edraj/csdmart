<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { _ } from "@/i18n";
  import { EyeSlashSolid, EyeSolid, LockSolid } from "flowbite-svelte-icons";
  import {
    OTP_TTL_MINUTES,
    ResetError,
    clearResetTarget,
    confirmPasswordReset,
    getResetTarget,
    isValidResetPassword,
    markResetCodeIssued,
    requestPasswordReset,
    resendSecondsRemaining,
    resetErrorKey,
    setResetDone,
    setResetStartOver,
    type ResetIdentifier,
  } from "@/lib/dmart_services/password_reset";
  import { setTitle } from "@/lib/title";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let target: ResetIdentifier | null = $state(null);
  let otp = $state("");
  let password = $state("");
  let confirmPassword = $state("");
  let showPassword = $state(false);
  let isSubmitting = $state(false);
  let formError = $state("");
  let errors: { otp?: string; password?: string; confirmPassword?: string } = $state({});

  // Countdown to the next allowed resend. The remaining time comes from when
  // the code was actually issued (step 1, or the last resend), not from when
  // this component mounted — otherwise a refresh here would cost the user
  // another full cooldown for a resend the server would already accept.
  let canResend = $state(false);
  let resendCountdown = $state(0);
  let resendTimer: ReturnType<typeof setInterval> | undefined;

  onMount(() => {
    target = getResetTarget();
    if (!target) {
      // Refreshed into a dead session, or opened the URL directly. Nothing to
      // verify against, so send them back to ask for a fresh code.
      setResetStartOver();
      goto("/reset-password");
      return;
    }
    startResendTimer();
  });

  $effect(() => setTitle($_("ChooseNewPassword")));

  function startResendTimer() {
    clearInterval(resendTimer);
    resendCountdown = resendSecondsRemaining();
    canResend = resendCountdown <= 0;
    if (canResend) return;
    resendTimer = setInterval(() => {
      resendCountdown--;
      if (resendCountdown <= 0) {
        canResend = true;
        clearInterval(resendTimer);
      }
    }, 1000);
  }

  onDestroy(() => {
    if (resendTimer) clearInterval(resendTimer);
  });

  async function handleResend() {
    if (!target || !canResend) return;
    canResend = false;
    formError = "";
    try {
      await requestPasswordReset(target);
      markResetCodeIssued();
      startResendTimer();
    } catch {
      formError = $_("ResetFailed");
      canResend = true;
    }
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (isSubmitting) return;
    if (!target) return;
    errors = {};
    formError = "";

    let valid = true;
    if (!otp.trim()) {
      errors.otp = $_("OtpRequired");
      valid = false;
    } else if (otp.trim().length !== 6) {
      errors.otp = $_("OtpInvalidLength");
      valid = false;
    }
    if (!password) {
      errors.password = $_("PasswordRequired");
      valid = false;
    } else if (!isValidResetPassword(password)) {
      errors.password = $_("PasswordRequirements");
      valid = false;
    }
    if (!confirmPassword) {
      errors.confirmPassword = $_("ConfirmPasswordRequired");
      valid = false;
    } else if (password !== confirmPassword) {
      errors.confirmPassword = $_("PasswordsDoNotMatch");
      valid = false;
    }
    if (!valid) return;

    isSubmitting = true;
    let succeeded = false;
    try {
      await confirmPasswordReset(target, otp.trim(), password);
      clearResetTarget();
      succeeded = true;
    } catch (e: unknown) {
      formError = e instanceof ResetError ? $_(resetErrorKey(e.reason)) : $_("ResetFailed");
    } finally {
      isSubmitting = false;
    }

    // Outside the try: the password is already changed server-side, so nothing
    // that happens here may be reported as a failed reset. setResetDone
    // swallows its own storage errors (private mode), and the navigation runs
    // either way — at worst the user misses the success notice.
    if (succeeded) {
      setResetDone();
      goto("/login");
    }
  }
</script>

<div class="auth-container">
  <div class="auth-content">
    {#if target}
      <div class="auth-header">
        <div class="icon-wrapper" aria-hidden="true"><LockSolid class="w-6 h-6" /></div>
        <h1 class="auth-title">{$_("ChooseNewPassword")}</h1>
        <p class="auth-description">
          {$_("ResetCodeSent", {
            values: { target: target.value, minutes: OTP_TTL_MINUTES },
          })}
        </p>
      </div>

      {#if formError}
        <div class="error-message" role="alert">{formError}</div>
      {/if}

      <form onsubmit={handleSubmit} class="auth-form" novalidate>
        <div class="form-group">
          <label for="otp" class="form-label">{$_("VerificationCode")}</label>
          <input
            id="otp"
            type="text"
            inputmode="numeric"
            maxlength="6"
            autocomplete="one-time-code"
            bind:value={otp}
            class="form-input"
            class:error={errors.otp}
            disabled={isSubmitting}
            aria-invalid={!!errors.otp}
            aria-describedby={errors.otp ? "otp-error" : undefined}
          />
          {#if errors.otp}
            <p id="otp-error" class="error-text-small" role="alert">{errors.otp}</p>
          {/if}
        </div>

        <div class="form-group">
          <label for="password" class="form-label">{$_("NewPassword")}</label>
          <div class="password-row">
            <input
              id="password"
              type={showPassword ? "text" : "password"}
              bind:value={password}
              class="form-input"
              class:error={errors.password}
              disabled={isSubmitting}
              autocomplete="new-password"
              aria-invalid={!!errors.password}
              aria-describedby="password-error"
            />
            <button
              type="button"
              class="password-toggle"
              aria-label={$_("TogglePasswordVisibility")}
              aria-pressed={showPassword}
              onclick={() => (showPassword = !showPassword)}
            >
              {#if showPassword}<EyeSlashSolid aria-hidden="true" />{:else}<EyeSolid aria-hidden="true" />{/if}
            </button>
          </div>
          <p
            id="password-error"
            class="error-text-small"
            class:hint={!errors.password}
            role={errors.password ? "alert" : undefined}
          >
            {errors.password ?? $_("PasswordRequirements")}
          </p>
        </div>

        <div class="form-group">
          <label for="confirm" class="form-label">
            {$_("ConfirmNewPassword")}
          </label>
          <input
            id="confirm"
            type={showPassword ? "text" : "password"}
            bind:value={confirmPassword}
            class="form-input"
            class:error={errors.confirmPassword}
            disabled={isSubmitting}
            autocomplete="new-password"
            aria-invalid={!!errors.confirmPassword}
            aria-describedby={errors.confirmPassword ? "confirm-error" : undefined}
          />
          {#if errors.confirmPassword}
            <p id="confirm-error" class="error-text-small" role="alert">
              {errors.confirmPassword}
            </p>
          {/if}
        </div>

        <button type="submit" class="submit-button" disabled={isSubmitting} aria-busy={isSubmitting}>
          {#if isSubmitting}<span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>{/if}
          {$_("UpdatePassword")}
        </button>
      </form>

      <div class="back-link">
        <button type="button" class="link-button" onclick={handleResend} disabled={!canResend}>
          {canResend
            ? $_("ResendCode")
            : $_("ResendCodeIn", { values: { seconds: resendCountdown } })}
        </button>
      </div>
    {/if}
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
  .error-text-small.hint {
    color: var(--color-text-muted);
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
    font-size: 0.875rem;
  }
  .link-button:hover:not(:disabled) {
    text-decoration: underline;
  }
  .password-row {
    display: flex;
    align-items: stretch;
    gap: 0.375rem;
  }
  .password-toggle {
    display: flex;
    align-items: center;
    padding: 0 0.625rem;
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    background: var(--color-surface);
    color: var(--color-text-muted);
    cursor: pointer;
  }
  .password-toggle:hover {
    color: var(--color-text);
    border-color: var(--color-border-strong);
  }
  .link-button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
