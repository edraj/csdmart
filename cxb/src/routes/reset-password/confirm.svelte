<script lang="ts">
  import { Button, Input, Label, Spinner } from "flowbite-svelte";
  import { EyeOutline, EyeSlashOutline } from "flowbite-svelte-icons";
  import { goto } from "@roxi/routify";
  import { onDestroy, onMount } from "svelte";
  import { _ } from "@/i18n";
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
  } from "@/lib/password_reset";
  import { ensureDmartAxios } from "@/lib/dmart_axios";
  import IconButton from "@/components/ui/IconButton.svelte";

  let target: ResetIdentifier | null = $state(null);
  let otp: string = $state("");
  let password: string = $state("");
  let confirmPassword: string = $state("");
  let showPassword: boolean = $state(false);
  let isSubmitting: boolean = $state(false);
  let formError: string | null = $state(null);
  let errors: { otp?: string; password?: string; confirmPassword?: string } = $state({});

  // Countdown to the next allowed resend. The remaining time comes from when
  // the code was actually issued (step 1, or the last resend), not from when
  // this component mounted — otherwise a refresh here would cost the user
  // another full cooldown for a resend the server would already accept.
  let canResend: boolean = $state(false);
  let resendCountdown: number = $state(0);
  let resendTimer: ReturnType<typeof setInterval> | undefined;

  onMount(() => {
    // This route lives outside /management, whose layout is where the axios
    // instance used to be created — make sure it exists before any request.
    ensureDmartAxios();
    target = getResetTarget();
    if (!target) {
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
    formError = null;
    try {
      await requestPasswordReset(target);
      markResetCodeIssued();
      startResendTimer();
    } catch {
      formError = $_("reset_failed");
      canResend = true;
    }
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (isSubmitting) return;
    if (!target) return;
    errors = {};
    formError = null;

    let valid = true;
    if (!otp.trim()) {
      errors.otp = $_("otp_required");
      valid = false;
    } else if (otp.trim().length !== 6) {
      errors.otp = $_("otp_invalid_length");
      valid = false;
    }
    if (!password) {
      errors.password = $_("password_required");
      valid = false;
    } else if (!isValidResetPassword(password)) {
      errors.password = $_("password_requirements");
      valid = false;
    }
    if (!confirmPassword) {
      errors.confirmPassword = $_("confirm_password_required");
      valid = false;
    } else if (password !== confirmPassword) {
      errors.confirmPassword = $_("passwords_do_not_match");
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
      formError = e instanceof ResetError ? $_(resetErrorKey(e.reason)) : $_("reset_failed");
    } finally {
      isSubmitting = false;
    }

    // Outside the try: the password is already changed server-side, so nothing
    // here may be reported as a failed reset. setResetDone swallows its own
    // storage errors and the navigation runs either way.
    if (succeeded) {
      setResetDone();
      // /management renders <Login /> for a signed-out user, which reads the
      // done flag and shows the success notice.
      $goto("/management");
    }
  }
</script>

<div class="flex justify-center items-center min-h-[calc(100svh-3.5rem)] px-4 bg-surface text-text">
  <div class="w-full max-w-md py-8">
    {#if target}
      <h1 class="text-2xl font-semibold text-text">{$_("choose_new_password")}</h1>
      <p class="mt-2 text-sm text-text-muted">
        {$_("reset_code_sent", {
          values: { target: target.value, minutes: OTP_TTL_MINUTES },
        })}
      </p>

      <form onsubmit={handleSubmit} class="mt-8 space-y-5">
        <div>
          <Label for="otp" class="mb-2">{$_("verification_code")}</Label>
          <Input
            id="otp"
            type="text"
            inputmode="numeric"
            maxlength={6}
            autocomplete="one-time-code"
            bind:value={otp}
            color={errors.otp ? "red" : "default"}
            aria-describedby={errors.otp ? "otp-error" : undefined}
            required
          />
          {#if errors.otp}<p id="otp-error" class="text-sm text-danger mt-2">{errors.otp}</p>{/if}
        </div>

        <div>
          <Label for="password" class="mb-2">{$_("new_password")}</Label>
          <div class="relative">
            <Input
              id="password"
              type={showPassword ? "text" : "password"}
              bind:value={password}
              color={errors.password ? "red" : "default"}
              autocomplete="new-password"
              aria-describedby="password-error"
              class="pe-11"
              required
            />
            <span class="absolute inset-y-0 end-1 flex items-center">
              <IconButton
                label={$_("toggle_password_visibility")}
                pressed={showPassword}
                controls="password"
                onclick={() => (showPassword = !showPassword)}
              >
                {#if showPassword}<EyeSlashOutline size="sm" />{:else}<EyeOutline size="sm" />{/if}
              </IconButton>
            </span>
          </div>
          <p id="password-error" class="mt-2 text-sm {errors.password ? 'text-danger' : 'text-text-muted'}">
            {errors.password ?? $_("password_requirements")}
          </p>
        </div>

        <div>
          <Label for="confirm" class="mb-2">{$_("confirm_new_password")}</Label>
          <Input
            id="confirm"
            type={showPassword ? "text" : "password"}
            bind:value={confirmPassword}
            color={errors.confirmPassword ? "red" : "default"}
            autocomplete="new-password"
            aria-describedby={errors.confirmPassword ? "confirm-error" : undefined}
            required
          />
          {#if errors.confirmPassword}
            <p id="confirm-error" class="text-sm text-danger mt-2">{errors.confirmPassword}</p>
          {/if}
        </div>

        <Button type="submit" color="primary" class="w-full" disabled={isSubmitting}>
          {#if isSubmitting}
            <Spinner class="me-3" size="4" />
          {/if}
          {$_("update_password")}
        </Button>

        {#if formError}<p class="text-sm text-danger" role="alert">{formError}</p>{/if}
      </form>

      <div class="mt-6 text-center">
        <Button color="alternative" onclick={handleResend} disabled={!canResend}>
          {canResend
            ? $_("resend_code")
            : $_("resend_code_in", { values: { seconds: resendCountdown } })}
        </Button>
      </div>
    {/if}
  </div>
</div>
