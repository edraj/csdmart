<script lang="ts">
  import { _ } from "@/i18n";
  import {
    ArrowLeftOutline,
    CheckCircleSolid,
    EnvelopeSolid,
    MailBoxOutline,
    MessagesSolid,
    UserSolid,
  } from "flowbite-svelte-icons";
  import { contactUs } from "@/stores/user";
  import { setTitle } from "@/lib/title";

  let name = $state("");
  let email = $state("");
  let subject = $state("");
  let message = $state("");
  let isSubmitting = $state(false);
  let showSuccess = $state(false);
  let showError = $state(false);
  let errors: { name?: string; message?: string } = $state({});

  const MESSAGE_MAX = 1000;

  function validateForm() {
    const newErrors: { name?: string; message?: string } = {};

    if (!name.trim()) {
      newErrors.name = $_("NameRequired");
    }

    if (!message.trim()) {
      newErrors.message = $_("MessageRequired");
    } else if (message.trim().length < 10) {
      newErrors.message = $_("MessageTooShort");
    } else if (message.trim().length > MESSAGE_MAX) {
      newErrors.message = $_("MessageTooLong");
    }

    errors = newErrors;
    return Object.keys(newErrors).length === 0;
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();

    if (!validateForm()) {
      return;
    }

    isSubmitting = true;
    showError = false;

    try {
      await contactUs(name, email, message, subject);

      name = "";
      email = "";
      subject = "";
      message = "";
      errors = {};
      showSuccess = true;

      setTimeout(() => {
        showSuccess = false;
      }, 5000);
    } catch {
      showError = true;
      setTimeout(() => {
        showError = false;
      }, 5000);
    } finally {
      isSubmitting = false;
    }
  }

  $effect(() => setTitle($_("ContactUs")));
</script>

<div class="contact-container">
  <div class="contact-content">
    <div class="contact-header">
      <button type="button" onclick={() => history.back()} class="btn-back">
        <ArrowLeftOutline class="w-4 h-4 rtl:rotate-180" aria-hidden="true" />
        {$_("Back")}
      </button>

      <div class="header-content">
        <div class="icon-wrapper" aria-hidden="true">
          <MessagesSolid class="header-icon w-6 h-6" />
        </div>
        <h1 class="contact-title">{$_("ContactUs")}</h1>
        <h2 class="contact-subtitle">{$_("ContactUsTitle")}</h2>
        <p class="contact-description">{$_("ContactUsDescription")}</p>
      </div>
    </div>

    {#if showSuccess}
      <div class="success-message" role="status">
        <CheckCircleSolid class="success-icon" aria-hidden="true" />
        <div class="success-content">
          <h3 class="success-title">{$_("MessageSent")}</h3>
          <p class="success-description">{$_("MessageSentDescription")}</p>
        </div>
      </div>
    {/if}

    {#if showError}
      <div class="error-message" role="alert">
        <svg
          class="shrink-0 inline w-4 h-4"
          aria-hidden="true"
          xmlns="http://www.w3.org/2000/svg"
          fill="currentColor"
          viewBox="0 0 20 20"
        >
          <path
            d="M10 .5a9.5 9.5 0 1 0 9.5 9.5A9.51 9.51 0 0 0 10 .5ZM9.5 4a1.5 1.5 0 1 1 0 3 1.5 1.5 0 0 1 0-3ZM12 15H8a1 1 0 0 1 0-2h1v-3H8a1 1 0 0 1 0-2h2a1 1 0 0 1 1 1v4h1a1 1 0 0 1 0 2Z"
          />
        </svg>
        <div class="error-content">
          <p class="error-text">{$_("MessageError")}</p>
        </div>
      </div>
    {/if}

    <div class="form-container">
      <form onsubmit={handleSubmit} class="contact-form" novalidate>
        <div class="form-group">
          <label for="name" class="form-label">
            <UserSolid class="label-icon" aria-hidden="true" />
            {$_("YourName")}
          </label>
          <input
            id="name"
            type="text"
            bind:value={name}
            placeholder={$_("YourNamePlaceholder")}
            class="form-input"
            class:error={errors.name}
            disabled={isSubmitting}
            autocomplete="name"
            aria-invalid={!!errors.name}
            aria-describedby={errors.name ? "name-error" : undefined}
          />
          {#if errors.name}
            <p id="name-error" class="error-text-small" role="alert">{errors.name}</p>
          {/if}
        </div>
        <div class="form-group">
          <label for="subject" class="form-label">
            <UserSolid class="label-icon" aria-hidden="true" />
            {$_("YourSubject")}
          </label>
          <input
            id="subject"
            type="text"
            bind:value={subject}
            placeholder={$_("YourSubjectPlaceholder")}
            class="form-input"
            disabled={isSubmitting}
          />
        </div>
        <div class="form-group">
          <label for="email" class="form-label">
            <MailBoxOutline class="label-icon" aria-hidden="true" />
            {$_("YourEmail")}
          </label>
          <input
            id="email"
            type="email"
            bind:value={email}
            placeholder={$_("YourEmailPlaceholder")}
            class="form-input"
            disabled={isSubmitting}
            autocomplete="email"
            inputmode="email"
          />
        </div>

        <div class="form-group">
          <label for="message" class="form-label">
            <EnvelopeSolid class="label-icon" aria-hidden="true" />
            {$_("YourMessage")}
          </label>
          <textarea
            id="message"
            bind:value={message}
            placeholder={$_("YourMessagePlaceholder")}
            rows="6"
            class="form-textarea"
            class:error={errors.message}
            disabled={isSubmitting}
            aria-invalid={!!errors.message}
            aria-describedby="message-count {errors.message ? 'message-error' : ''}"
          ></textarea>
          <div class="character-count" id="message-count">
            <span class:over-limit={message.length > MESSAGE_MAX}>
              {message.length}/{MESSAGE_MAX}
            </span>
          </div>
          {#if errors.message}
            <p id="message-error" class="error-text-small" role="alert">{errors.message}</p>
          {/if}
        </div>

        <button
          type="submit"
          class="submit-button"
          class:loading={isSubmitting}
          disabled={isSubmitting}
          aria-busy={isSubmitting}
        >
          {#if isSubmitting}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {$_("SendingMessage")}
          {:else}
            <EnvelopeSolid class="button-icon" aria-hidden="true" />
            {$_("SendMessage")}
          {/if}
        </button>
      </form>
    </div>

    <div class="additional-info">
      <div class="info-card">
        <MessagesSolid class="info-icon" aria-hidden="true" />
        <div class="info-content">
          <h3 class="info-title">{$_("Welcome")}</h3>
          <p class="info-description">{$_("ContactUsFeedback")}</p>
        </div>
      </div>
    </div>
  </div>
</div>

<style>
  .contact-container {
    min-height: 100vh;
    background: var(--gradient-page);
    padding: 2rem var(--space-page-x);
  }

  .contact-content {
    max-width: 560px;
    margin: 0 auto;
    animation: fadeInUp var(--duration-slow) var(--ease-out);
  }

  .contact-header {
    text-align: center;
    margin-bottom: 1.5rem;
  }

  .contact-header .btn-back {
    float: inline-start;
  }

  .header-content {
    clear: both;
    padding-top: 0.5rem;
    margin-bottom: 1.5rem;
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

  .contact-title {
    font-size: 2rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.375rem;
    letter-spacing: -0.02em;
  }

  .contact-subtitle {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-text-muted);
    margin-bottom: 0.75rem;
  }

  .contact-description {
    font-size: 0.9375rem;
    color: var(--color-text-muted);
    line-height: 1.6;
  }

  .success-message, .error-message {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.875rem 1rem;
    border-radius: var(--radius-card);
    margin-bottom: 1.5rem;
    animation: fadeInDown var(--duration-normal) var(--ease-out);
  }

  .success-message { background: var(--color-success-bg); border: 1px solid var(--color-success-border); }
  .error-message { background: var(--color-danger-bg); border: 1px solid var(--color-danger-border); color: var(--color-danger-fg); }

  .success-message :global(.success-icon) { width: 1.25rem; height: 1.25rem; color: var(--color-success); flex-shrink: 0; }

  .success-title {
    font-weight: 600;
    color: var(--color-success-fg);
    margin-bottom: 0.125rem;
    font-size: 0.875rem;
  }

  .success-description { color: var(--color-text-muted); font-size: 0.8125rem; }
  .error-text { color: var(--color-danger-fg); font-size: 0.8125rem; font-weight: 500; }

  .form-container {
    background: var(--color-surface-2);
    border-radius: var(--radius-modal);
    padding: 2rem;
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    margin-bottom: 1.5rem;
  }

  .contact-form {
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

  .form-label :global(.label-icon) { width: 0.875rem; height: 0.875rem; color: var(--color-text-faint); }

  .form-input, .form-textarea {
    padding: 0.6875rem 0.875rem;
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    font-size: 0.9375rem;
    transition: all var(--duration-normal) var(--ease-out);
    background: var(--color-surface);
    color: var(--color-text);
  }

  .form-input::placeholder, .form-textarea::placeholder { color: var(--color-text-faint); }
  .form-input:hover, .form-textarea:hover { border-color: var(--color-border-strong); }

  .form-input:focus, .form-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
    background: var(--color-surface-2);
  }

  .form-input.error, .form-textarea.error {
    border-color: var(--color-danger);
    box-shadow: 0 0 0 3px var(--color-danger-soft);
  }

  .form-textarea { resize: vertical; min-height: 100px; font-family: inherit; }

  .character-count { font-size: 0.6875rem; color: var(--color-text-faint); text-align: end; font-variant-numeric: tabular-nums; }
  .over-limit { color: var(--color-danger); font-weight: 600; }

  .error-text-small { font-size: 0.75rem; color: var(--color-danger); font-weight: 500; }

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

  .submit-button :global(.button-icon) { width: 1rem; height: 1rem; }

  .submit-button:hover:not(:disabled) {
    background: var(--gradient-brand-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-brand-lg);
  }

  .submit-button:active:not(:disabled) { transform: translateY(0); }
  .submit-button:disabled { opacity: 0.6; cursor: not-allowed; transform: none; }

  .additional-info { margin-top: 1.5rem; }

  .info-card {
    background: var(--color-surface-2);
    border-radius: var(--radius-card);
    padding: 1.25rem;
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    display: flex;
    align-items: center;
    gap: 0.875rem;
  }

  .info-card :global(.info-icon) { width: 1.5rem; height: 1.5rem; color: var(--color-primary); flex-shrink: 0; }

  .info-title { font-weight: 600; color: var(--color-text); margin-bottom: 0.25rem; font-size: 0.9375rem; }
  .info-description { color: var(--color-text-muted); font-size: 0.8125rem; line-height: 1.5; }

  @media (max-width: 640px) {
    .contact-container { padding: 1rem var(--space-page-x); }
    .contact-title { font-size: 1.5rem; }
    .contact-subtitle { font-size: 1.0625rem; }
    .form-container { padding: 1.5rem; border-radius: var(--radius-card); }
    .info-card { flex-direction: column; text-align: center; }
  }
</style>
