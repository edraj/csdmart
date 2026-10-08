<script lang="ts">
  import { Button, Input, Label, Spinner } from "flowbite-svelte";
  import { goto, url } from "@roxi/routify";
  import { onMount } from "svelte";
  import { _ } from "@/i18n";
  import {
    clearResetTarget,
    consumeResetStartOver,
    detectIdentifier,
    markResetCodeIssued,
    requestPasswordReset,
    setResetTarget,
  } from "@/lib/password_reset";
  import { ensureDmartAxios } from "@/lib/dmart_axios";

  let rawIdentifier: string = $state("");
  let isSubmitting: boolean = $state(false);
  let fieldError: string | null = $state(null);
  let formError: string | null = $state(null);
  let startOver: boolean = $state(false);

  onMount(() => {
    // This route lives outside /management, whose layout is where the axios
    // instance used to be created — make sure it exists before any request.
    ensureDmartAxios();
    startOver = consumeResetStartOver();
  });

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (isSubmitting) return;
    fieldError = null;
    formError = null;
    // Drop any target left over from an earlier attempt: if this request fails
    // we must not leave step 2 announcing a stale address.
    clearResetTarget();

    const id = detectIdentifier(rawIdentifier);
    if (!id) {
      fieldError = $_("invalid_email_or_phone");
      return;
    }

    isSubmitting = true;
    try {
      // A 2xx says nothing about whether the account exists — the endpoint
      // answers identically for unknown users and for the resend cooldown.
      await requestPasswordReset(id);
      setResetTarget(id);
      // Stamp the issue time so step 2's resend countdown reflects the
      // server's cooldown rather than restarting on every mount.
      markResetCodeIssued();
      $goto("/reset-password/confirm");
    } catch {
      formError = $_("reset_failed");
    } finally {
      isSubmitting = false;
    }
  }
</script>

<div class="flex justify-center items-center min-h-[calc(100svh-3.5rem)] px-4 bg-surface text-text">
  <div class="w-full max-w-md py-8">
    <h1 class="text-2xl font-semibold text-text">{$_("reset_password")}</h1>
    <p class="mt-2 text-sm text-text-muted">{$_("reset_password_intro")}</p>

    {#if startOver}
      <p class="mt-4 text-sm text-warning" role="status">{$_("reset_start_over")}</p>
    {/if}

    <form onsubmit={handleSubmit} class="mt-8 space-y-5">
      <div>
        <Label for="identifier" class="mb-2">{$_("email_or_phone")}</Label>
        <Input
          id="identifier"
          type="text"
          placeholder={$_("email_or_phone")}
          bind:value={rawIdentifier}
          color={fieldError ? "red" : "default"}
          autocomplete="username"
          aria-describedby={fieldError ? "identifier-error" : undefined}
          required
        />
        {#if fieldError}
          <p id="identifier-error" class="text-sm text-danger mt-2">{fieldError}</p>
        {/if}
      </div>

      <Button type="submit" color="primary" class="w-full" disabled={isSubmitting}>
        {#if isSubmitting}
          <Spinner class="me-3" size="4" />
        {/if}
        {$_("send_reset_code")}
      </Button>

      {#if formError}
        <p class="text-sm text-danger" role="alert">{formError}</p>
      {/if}
    </form>

    <div class="mt-6 text-center">
      <Button color="alternative" href={$url("/management")}>
        {$_("back_to_login")}
      </Button>
    </div>
  </div>
</div>
