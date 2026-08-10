<script lang="ts">
  import { onDestroy, onMount } from "svelte";
  import { goto } from "@roxi/routify";
  import { _, locale } from "@/i18n";
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

  $goto;

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
  let resendTimer: any;

  const isRTL = $derived($locale === "ar" || $locale === "ku");

  onMount(() => {
    target = getResetTarget();
    if (!target) {
      // Refreshed into a dead session, or opened the URL directly. Nothing to
      // verify against, so send them back to ask for a fresh code.
      setResetStartOver();
      $goto("/reset-password");
      return;
    }
    startResendTimer();
  });

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
    } catch (e: any) {
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
      $goto("/login");
    }
  }
</script>

<div class="auth-container">
  <div class="auth-content">
    {#if target}
      <div class="auth-header">
        <div class="icon-wrapper"><LockSolid class="text-white w-6 h-6" /></div>
        <h1 class="auth-title">{$_("ChooseNewPassword")}</h1>
        <p class="auth-description">
          {$_("ResetCodeSent", {
            values: { target: target.value, minutes: OTP_TTL_MINUTES },
          })}
        </p>
      </div>

      {#if formError}
        <div class="error-message" class:rtl={isRTL} role="alert">{formError}</div>
      {/if}

      <form onsubmit={handleSubmit} class="auth-form">
        <div class="form-group">
          <label for="otp" class="form-label" class:rtl={isRTL}>{$_("VerificationCode")}</label>
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
            <p id="otp-error" class="error-text-small" class:rtl={isRTL} role="alert">{errors.otp}</p>
          {/if}
        </div>

        <div class="form-group">
          <label for="password" class="form-label" class:rtl={isRTL}>{$_("NewPassword")}</label>
          <div class="password-row">
            <input
              id="password"
              type={showPassword ? "text" : "password"}
              bind:value={password}
              class="form-input"
              class:error={errors.password}
              class:rtl={isRTL}
              disabled={isSubmitting}
              autocomplete="new-password"
              aria-invalid={!!errors.password}
              aria-describedby={errors.password ? "password-error" : undefined}
            />
            <button
              type="button"
              class="password-toggle"
              aria-label={$_("TogglePasswordVisibility")}
              aria-pressed={showPassword}
              onclick={() => (showPassword = !showPassword)}
            >
              {#if showPassword}<EyeSlashSolid />{:else}<EyeSolid />{/if}
            </button>
          </div>
          <p
            id="password-error"
            class="error-text-small"
            class:rtl={isRTL}
            role={errors.password ? "alert" : undefined}
          >
            {errors.password ?? $_("PasswordRequirements")}
          </p>
        </div>

        <div class="form-group">
          <label for="confirm" class="form-label" class:rtl={isRTL}>
            {$_("ConfirmNewPassword")}
          </label>
          <input
            id="confirm"
            type={showPassword ? "text" : "password"}
            bind:value={confirmPassword}
            class="form-input"
            class:error={errors.confirmPassword}
            class:rtl={isRTL}
            disabled={isSubmitting}
            autocomplete="new-password"
            aria-invalid={!!errors.confirmPassword}
            aria-describedby={errors.confirmPassword ? "confirm-error" : undefined}
          />
          {#if errors.confirmPassword}
            <p id="confirm-error" class="error-text-small" class:rtl={isRTL} role="alert">
              {errors.confirmPassword}
            </p>
          {/if}
        </div>

        <button type="submit" class="submit-button" class:rtl={isRTL} disabled={isSubmitting}>
          {#if isSubmitting}<div class="loading-spinner"></div>{/if}
          {$_("UpdatePassword")}
        </button>
      </form>

      <div class="back-link" class:rtl={isRTL}>
        <button class="link-button" onclick={handleResend} disabled={!canResend}>
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
  .password-row {
    display: flex;
    align-items: stretch;
    gap: 0.375rem;
  }
  .password-toggle {
    display: flex;
    align-items: center;
    padding: 0 0.625rem;
    border: 1px solid var(--color-gray-300);
    border-radius: 0.5rem;
    background: transparent;
    cursor: pointer;
  }
  .link-button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
