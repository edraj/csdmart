<script lang="ts">
  import { Button, ButtonGroup, Heading, Input, Label, Spinner } from "flowbite-svelte";
  import { EyeSlashSolid, EyeSolid } from "flowbite-svelte-icons";
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

  $goto;

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
  let resendTimer: any;

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
    } catch (e: any) {
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

<div class="flex justify-center items-center h-svh">
  <div class="w-full max-w-md p-8">
    {#if target}
      <Heading class="text-primary" tag="h2">{$_("choose_new_password")}</Heading>
      <p class="mt-2 text-sm opacity-75">
        {$_("reset_code_sent", {
          values: { target: target.value, minutes: OTP_TTL_MINUTES },
        })}
      </p>

      <form onsubmit={handleSubmit} class="mt-8">
        <Label for="otp">{$_("verification_code")}</Label>
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
        {#if errors.otp}<p id="otp-error" class="text-red-600 mt-2">{errors.otp}</p>{/if}

        <div class="mt-6"></div>
        <Label for="password">{$_("new_password")}</Label>
        <ButtonGroup class="w-full">
          <Input
            id="password"
            type={showPassword ? "text" : "password"}
            bind:value={password}
            color={errors.password ? "red" : "default"}
            autocomplete="new-password"
            aria-describedby={errors.password ? "password-error" : undefined}
            required
          />
          <Button class="flex items-center border-s-0" color="light"
                  onclick={() => (showPassword = !showPassword)} aria-controls="password"
                  aria-label={$_("toggle_password_visibility")} aria-pressed={showPassword}>
            {#if showPassword}<EyeSolid />{:else}<EyeSlashSolid />{/if}
          </Button>
        </ButtonGroup>
        <p id="password-error" class="mt-2 text-sm" class:text-red-600={!!errors.password}>
          {errors.password ?? $_("password_requirements")}
        </p>

        <div class="mt-6"></div>
        <Label for="confirm">{$_("confirm_new_password")}</Label>
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
          <p id="confirm-error" class="text-red-600 mt-2">{errors.confirmPassword}</p>
        {/if}

        <div class="mt-6"></div>
        <Button type="submit" class="w-full bg-primary" disabled={isSubmitting}
                style="cursor: pointer">
          {#if isSubmitting}
            <Spinner class="me-3" size="4" color="blue" />
          {/if}
          {$_("update_password")}
        </Button>

        {#if formError}<p class="text-red-600 mt-2">{formError}</p>{/if}
      </form>

      <div class="mt-6 text-center">
        <Button color="light" onclick={handleResend} disabled={!canResend}
                style="cursor: pointer">
          {canResend
            ? $_("resend_code")
            : $_("resend_code_in", { values: { seconds: resendCountdown } })}
        </Button>
      </div>
    {/if}
  </div>
</div>
