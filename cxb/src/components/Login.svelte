<script lang="ts">
    import {Button, ButtonGroup, Heading, Input, InputAddon, Label, Spinner,} from "flowbite-svelte";
    import {EyeSlashSolid, EyeSolid} from 'flowbite-svelte-icons';
    import {goto} from "@roxi/routify";
    import {signin} from "@/stores/user";
    import {_} from "@/i18n";
    import {onMount} from "svelte";
    import {consumeResetDone} from "@/lib/password_reset";

    $goto;

  let username: string = $state("");
  let password: string = $state("");
  let errorMessage: string | null = $state(null);
  let showPassword: boolean = $state(false);
  let isLoginLoading: boolean = $state(false);
  // Set by reset-password/confirm.svelte just before it navigates here.
  let resetSuccess: boolean = $state(false);

  onMount(() => {
    resetSuccess = consumeResetDone();
  });

  async function handleSubmit(event: Event) {
    event.preventDefault();
      errorMessage = null;
      isLoginLoading = false;
    try {
        isLoginLoading = true;
        await signin(username, password);
        window.location.reload();
    } catch (error: any) {
        errorMessage = error.response?.data?.error?.message ?? "Something went wrong, please try again.";
    }
    isLoginLoading = false;
  }

  function togglePasswordVisibility() {
    showPassword = !showPassword;
  }
</script>

<div class="flex row h-svh">
  <div class="flex justify-center w-1/2 p-12 flex flex-col">
    <Heading class="text-primary">
      Welcome back <br>
      log in to your account
    </Heading>

    {#if resetSuccess}
      <p class="text-green-600 mt-4" role="status">{$_("reset_password_success")}</p>
    {/if}

    <form onsubmit={handleSubmit} class="mt-12">
        <Label for="username">{$_("username")}</Label>
        <Input
          id="username"
          placeholder={$_("shortname")}
          type="text"
          bind:value={username}
          color={errorMessage ? "red" : "default"}
          required
        />
        <div class="mt-6"></div>
        <Label for="password">{$_("password")}</Label>
        <ButtonGroup class="w-full">
            <Input
              id="password"
              placeholder={$_("password")}
              type={showPassword ? "text" : "password"}
              bind:value={password}
              color={errorMessage ? "red" : "default"}
              minlength={8}
              maxlength={24}
              required
            />
            <Button class="flex items-center border-s-0" color="light"
                    onclick={togglePasswordVisibility} aria-controls="password">
              {#if showPassword}
                <EyeSolid />
              {:else}
                <EyeSlashSolid />
              {/if}
            </Button>
        </ButtonGroup>
      <div class="mt-6"></div>
        <Button type="submit" class="w-full bg-primary" style="cursor: pointer">
            {#if isLoginLoading}
                <Spinner class="me-3" size="4" color="blue" />
                Loading ...
            {:else}
                {$_("login")}
            {/if}
        </Button>
        {#if errorMessage}
            <p class="text-red-600 mt-2">{errorMessage}</p>
        {/if}
        <div class="mt-4 text-center">
            <button type="button" class="text-primary underline"
                    style="cursor: pointer"
                    onclick={() => $goto('/reset-password')}>
                {$_("forgot_password")}
            </button>
        </div>
    </form>
  </div>
  <div class="flex content-center items-center justify-center w-1/2 bg-primary"></div>
</div>
