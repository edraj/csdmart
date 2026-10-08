<script lang="ts">
  import { goto as gotoStore } from "@roxi/routify";
  import { _ } from "@/i18n";
  import { EyeSlashSolid, EyeSolid, LockSolid, UserSolid } from "flowbite-svelte-icons";
  import { loginBy, signin } from "@/stores/user";
  import { onMount } from "svelte";
  import { consumeResetDone } from "@/lib/dmart_services/password_reset";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;
  let identifier = $state("");
  let password = $state("");
  let showPassword = $state(false);
  let isSubmitting = $state(false);
  let showError = $state(false);
  let errors: { identifier?: string; password?: string } = $state({});

  // Set by the confirm route before it redirects here.
  let resetSuccess = $state(false);
  onMount(() => {
    resetSuccess = consumeResetDone();
  });

  $effect(() => setTitle($_("SignIn")));

  function isEmail(input: string): boolean {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(input);
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    showError = false;
    errors = {};
    isSubmitting = true;

    const trimmedIdentifier = identifier.trim();

    if (!trimmedIdentifier || !password) {
      if (!trimmedIdentifier) errors.identifier = $_("ThisFieldIsRequired");
      if (!password) errors.password = $_("ThisFieldIsRequired");
      isSubmitting = false;
      return;
    }

    try {
      if (isEmail(trimmedIdentifier)) {
        await loginBy(trimmedIdentifier, password);
      } else {
        await signin(trimmedIdentifier, password);
      }
      goto("/dashboard");
    } catch {
      showError = true;
    } finally {
      isSubmitting = false;
    }
  }
</script>

<div class="login-container">
  <div class="login-content">
    <div class="login-header">
      <div class="icon-wrapper" aria-hidden="true">
        <UserSolid class="w-6 h-6" />
      </div>
      <h1 class="login-title">{$_("WelcomeBack")}</h1>
      <p class="login-description">{$_("PleaseSignInToContinue")}</p>
    </div>

    {#if resetSuccess}
      <div class="success-message" role="status">{$_("ResetPasswordSuccess")}</div>
    {/if}

    {#if showError}
      <div class="error-message" role="alert">
        <svg
          class="shrink-0 w-4 h-4"
          aria-hidden="true"
          xmlns="http://www.w3.org/2000/svg"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            d="M10 .5a9.5 9.5 0 1 0 9.5 9.5A9.51 9.51 0 0 0 10 .5ZM9.5 4a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3ZM12 15H8a1 1 0 0 1 0-2h1v-3H8a1 1 0 0 1 0-2h2a1 1 0 0 1 1 1v4h1a1 1 0 0 1 0 2Z"
          />
        </svg>
        <p class="error-text">{$_("InvalidCredentials")}</p>
      </div>
    {/if}

    <div class="form-container">
      <form onsubmit={handleSubmit} class="login-form" novalidate>
        <div class="form-group">
          <label for="identifier" class="form-label">
            <UserSolid class="label-icon" aria-hidden="true" />
            {$_("Username")} / {$_("Email")}
          </label>
          <input
            id="identifier"
            type="text"
            bind:value={identifier}
            placeholder={$_("Username") + " " + $_("or") + " " + $_("Email")}
            class="form-input"
            class:error={errors.identifier}
            disabled={isSubmitting}
            autocomplete="username"
            aria-invalid={!!errors.identifier}
            aria-describedby={errors.identifier ? "identifier-error" : undefined}
          />
          {#if errors.identifier}
            <p id="identifier-error" class="error-text-small" role="alert">
              {errors.identifier}
            </p>
          {/if}
        </div>

        <div class="form-group">
          <label for="password" class="form-label">
            <LockSolid class="label-icon" aria-hidden="true" />
            {$_("Password")}
          </label>
          <div class="password-input-wrapper">
            <input
              id="password"
              type={showPassword ? "text" : "password"}
              bind:value={password}
              placeholder={$_("Password")}
              class="form-input password-input"
              class:error={errors.password}
              disabled={isSubmitting}
              autocomplete="current-password"
              aria-invalid={!!errors.password}
              aria-describedby={errors.password ? "password-error" : undefined}
            />
            <button
              aria-label={$_("TogglePasswordVisibility")}
              aria-pressed={showPassword}
              type="button"
              class="password-toggle"
              onclick={() => (showPassword = !showPassword)}
            >
              {#if showPassword}
                <EyeSlashSolid class="toggle-icon" aria-hidden="true" />
              {:else}
                <EyeSolid class="toggle-icon" aria-hidden="true" />
              {/if}
            </button>
          </div>
          {#if errors.password}
            <p id="password-error" class="error-text-small" role="alert">
              {errors.password}
            </p>
          {/if}
        </div>

        <button
          type="submit"
          class="submit-button"
          disabled={isSubmitting}
          aria-busy={isSubmitting}
        >
          {#if isSubmitting}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {$_("SigningIn")}
          {:else}
            <UserSolid class="button-icon" aria-hidden="true" />
            {$_("SignIn")}
          {/if}
        </button>
      </form>

      <div class="register-link">
        <span class="register-text">{$_("DontHaveAccount")}</span>
        <a class="link-button" href={withBase("/register")}>{$_("Register")}</a>
      </div>

      <div class="forgot-link">
        <a class="link-button" href={withBase("/reset-password")}>{$_("ForgotPassword")}</a>
      </div>

      <div class="terms-text">
        <p>{$_("TermsAndConditions")}</p>
      </div>
    </div>
  </div>
</div>

<style>
  .login-container {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--gradient-page);
    padding: 2rem var(--space-page-x);
    position: relative;
    overflow: hidden;
  }

  .login-container::before {
    content: "";
    position: absolute;
    top: -40%;
    inset-inline-end: -20%;
    width: 60%;
    height: 80%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.06) 0%, transparent 70%);
    pointer-events: none;
  }

  .login-container::after {
    content: "";
    position: absolute;
    bottom: -30%;
    inset-inline-start: -15%;
    width: 50%;
    height: 60%;
    background: radial-gradient(circle, rgba(139, 92, 246, 0.05) 0%, transparent 70%);
    pointer-events: none;
  }

  .login-content {
    max-width: 420px;
    width: 100%;
    position: relative;
    z-index: 1;
    animation: fadeInUp var(--duration-slow) var(--ease-out);
  }

  .login-header {
    text-align: center;
    margin-bottom: 2rem;
  }

  .icon-wrapper {
    width: 3.5rem;
    height: 3.5rem;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    border-radius: var(--radius-card);
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.25rem auto;
    box-shadow: var(--shadow-brand);
  }

  .login-title {
    font-size: 1.75rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.5rem;
    letter-spacing: -0.02em;
  }

  .login-description {
    font-size: 0.9375rem;
    color: var(--color-text-muted);
    line-height: 1.5;
  }

  .error-message {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.875rem 1rem;
    border-radius: var(--radius-card);
    margin-bottom: 1.5rem;
    background: var(--color-danger-bg);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-fg);
    animation: fadeInDown var(--duration-normal) var(--ease-out);
  }

  .success-message {
    padding: 0.75rem 1rem;
    border-radius: var(--radius-card);
    background: var(--color-success-bg);
    border: 1px solid var(--color-success-border);
    color: var(--color-success-fg);
    font-size: 0.875rem;
    margin-bottom: 1rem;
  }

  .error-text {
    font-size: 0.8125rem;
    font-weight: 500;
  }

  .form-container {
    background: var(--color-surface-2);
    border-radius: var(--radius-modal);
    padding: 2rem;
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    animation: fadeInUp var(--duration-slow) var(--ease-out) 0.1s both;
  }

  .login-form {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
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
    font-weight: 500;
    color: var(--color-text);
    font-size: 0.8125rem;
  }

  .form-label :global(.label-icon) {
    width: 0.875rem;
    height: 0.875rem;
    color: var(--color-text-faint);
  }

  .form-input {
    padding: 0.6875rem 0.875rem;
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    font-size: 0.9375rem;
    transition: all var(--duration-normal) var(--ease-out);
    background: var(--color-surface);
    color: var(--color-text);
  }

  .form-input::placeholder {
    color: var(--color-text-faint);
  }

  .form-input:hover {
    border-color: var(--color-border-strong);
  }

  .form-input:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
    background: var(--color-surface-2);
  }

  .form-input.error {
    border-color: var(--color-danger);
    box-shadow: 0 0 0 3px var(--color-danger-soft);
  }

  .password-input-wrapper {
    position: relative;
    display: flex;
    align-items: center;
  }

  .password-input {
    padding-inline-end: 2.75rem;
    width: 100%;
  }

  .password-toggle {
    position: absolute;
    inset-inline-end: 0.625rem;
    background: none;
    border: none;
    cursor: pointer;
    color: var(--color-text-faint);
    padding: 0.25rem;
    border-radius: var(--radius-control);
    transition: color var(--duration-fast) ease;
    display: inline-flex;
  }

  .password-toggle:hover {
    color: var(--color-text);
  }

  .password-toggle :global(.toggle-icon) {
    width: 1.125rem;
    height: 1.125rem;
  }

  .error-text-small {
    font-size: 0.75rem;
    color: var(--color-danger);
    font-weight: 500;
  }

  .submit-button {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    font-weight: 600;
    padding: 0.75rem 1.5rem;
    border-radius: var(--radius-control);
    border: none;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem;
    box-shadow: var(--shadow-brand);
    margin-top: 0.25rem;
  }

  .submit-button :global(.button-icon) {
    width: 1rem;
    height: 1rem;
  }

  .submit-button:hover:not(:disabled) {
    background: var(--gradient-brand-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-brand-lg);
  }

  .submit-button:active:not(:disabled) {
    transform: translateY(0);
    box-shadow: var(--shadow-xs);
  }

  .submit-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
    transform: none;
  }

  .register-link {
    text-align: center;
    margin-top: 1.5rem;
    padding-top: 1.25rem;
    border-top: 1px solid var(--color-border);
  }

  /* Its own row below the register row, but deliberately without a second
     border-top: one divider above the whole block is enough. */
  .forgot-link {
    text-align: center;
    margin-top: 0.75rem;
  }

  .register-text {
    color: var(--color-text-muted);
    font-size: 0.8125rem;
  }

  .link-button {
    background: none;
    border: none;
    color: var(--color-primary);
    font-weight: 600;
    cursor: pointer;
    text-decoration: none;
    font-size: 0.8125rem;
    margin-inline-start: 0.25rem;
    transition: color var(--duration-fast) ease;
  }

  .link-button:hover {
    color: var(--color-primary-hover);
    text-decoration: underline;
  }

  .terms-text {
    text-align: center;
    margin-top: 1rem;
    font-size: 0.6875rem;
    color: var(--color-text-faint);
    line-height: 1.5;
  }

  @media (max-width: 640px) {
    .login-container {
      padding: 3rem var(--space-page-x) 1rem;
      align-items: flex-start;
    }

    .login-title {
      font-size: 1.5rem;
    }

    .form-container {
      padding: 1.5rem;
      border-radius: var(--radius-card);
    }
  }
</style>
