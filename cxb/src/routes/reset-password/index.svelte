<script lang="ts">
  import { Button, Heading, Input, Label, Spinner } from "flowbite-svelte";
  import { goto } from "@roxi/routify";
  import { onMount } from "svelte";
  import { _ } from "@/i18n";
  import {
    clearResetTarget,
    consumeResetStartOver,
    detectIdentifier,
    requestPasswordReset,
    setResetTarget,
  } from "@/lib/password_reset";
  import { ensureDmartAxios } from "@/lib/dmart_axios";

  $goto;

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
      $goto("/reset-password/confirm");
    } catch {
      formError = $_("reset_failed");
    } finally {
      isSubmitting = false;
    }
  }
</script>

<div class="flex justify-center items-center h-svh">
  <div class="w-full max-w-md p-8">
    <Heading class="text-primary" tag="h2">{$_("reset_password")}</Heading>
    <p class="mt-2 text-sm opacity-75">{$_("reset_password_intro")}</p>

    {#if startOver}
      <p class="text-amber-600 mt-4">{$_("reset_start_over")}</p>
    {/if}

    <form onsubmit={handleSubmit} class="mt-8">
      <Label for="identifier">{$_("email_or_phone")}</Label>
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
        <p id="identifier-error" class="text-red-600 mt-2">{fieldError}</p>
      {/if}

      <div class="mt-6"></div>
      <Button type="submit" class="w-full bg-primary" disabled={isSubmitting}
              style="cursor: pointer">
        {#if isSubmitting}
          <Spinner class="me-3" size="4" color="blue" />
        {/if}
        {$_("send_reset_code")}
      </Button>

      {#if formError}
        <p class="text-red-600 mt-2">{formError}</p>
      {/if}
    </form>

    <div class="mt-6 text-center">
      <Button color="light" onclick={() => $goto("/management")} style="cursor: pointer">
        {$_("back_to_login")}
      </Button>
    </div>
  </div>
</div>
