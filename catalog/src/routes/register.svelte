<script lang="ts">
  import { onDestroy } from "svelte";
  import { goto as gotoStore } from "@roxi/routify";
  import { _ } from "@/i18n";
  import { checkExisting, register, requestOtp } from "@/stores/user";

  import {
    ArrowLeftOutline,
    EnvelopeSolid,
    EyeSlashSolid,
    EyeSolid,
    LockSolid,
    PhoneSolid,
    UserSolid,
  } from "flowbite-svelte-icons";
  import { getEntity } from "@/lib/dmart_services";
  import { ResourceType } from "@edraj/tsdmart";
  import { getCurrentScope } from "@/stores/user";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // Routify's helpers read the fragment context when first subscribed, and
  // Svelte 5 subscribes to a `$store` lazily on first read — so a `$gotoStore`
  // first touched inside an async callback logs "Unable to access context".
  // Capture the navigate function once, during component init.
  const goto = $gotoStore;

  let formData = $state({
    email: "",
    phoneNumber: "",
    password: "",
    confirmPassword: "",
    gender: "",
    age: "",
    address: "",
    confessorsText: "",
    confessors: [] as string[],
    profession: "",
    description: "",
  });

  let agreeToTerms = $state(false);
  let showPassword = $state(false);
  let showConfirmPassword = $state(false);
  let isSubmitting = $state(false);
  let showError = $state(false);
  let otpCode = $state("");
  let isOtpStep = $state(false);
  let isVerifyingOtp = $state(false);
  let canResendOtp = $state(false);
  let resendCountdown = $state(60);
  let resendTimer: ReturnType<typeof setInterval> | undefined;

  let showAdditionalFields = $state(false);

  type Errors = {
    email?: string;
    phoneNumber?: string;
    password?: string;
    confirmPassword?: string;
    gender?: string;
    age?: string;
    address?: string;
    confessors?: string;
    profession?: string;
    description?: string;
    terms?: string;
    otp?: string;
  };
  let errors: Errors = $state({});

  $effect(() => setTitle(isOtpStep ? $_("VerifyEmail") : $_("CreateAccount")));

  function messageOf(error: unknown): string {
    return error instanceof Error ? error.message : String(error ?? "");
  }

  function parseConfessors(text: string): string[] {
    if (!text.trim()) return [];

    return text
      .split(/[,;\n]/)
      .map((item) => item.trim())
      .filter((item) => item.length > 0);
  }

  $effect(() => {
    formData.confessors = parseConfessors(formData.confessorsText);
  });

  async function handleSubmit(event: Event) {
    event.preventDefault();

    if (isOtpStep) {
      await handleOtpVerification();
      return;
    }

    errors = {};

    let isValid = true;

    const trimmedEmail = formData.email.trim();
    const trimmedPhoneNumber = formData.phoneNumber.trim();

    if (!trimmedEmail) {
      errors.email = $_("EmailRequired");
      isValid = false;
    } else if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmedEmail)) {
      errors.email = $_("InvalidEmail");
      isValid = false;
    }

    if (!formData.password) {
      errors.password = $_("PasswordRequired");
      isValid = false;
    } else if (formData.password.length < 6) {
      errors.password = $_("PasswordTooShort");
      isValid = false;
    }

    if (!formData.confirmPassword) {
      errors.confirmPassword = $_("ConfirmPasswordRequired");
      isValid = false;
    } else if (formData.password !== formData.confirmPassword) {
      errors.confirmPassword = $_("PasswordsDoNotMatch");
      isValid = false;
    }

    if (!formData.gender) {
      errors.gender = $_("GenderRequired");
      isValid = false;
    }

    if (
      formData.age &&
      (isNaN(Number(formData.age)) ||
        Number(formData.age) < 1 ||
        Number(formData.age) > 150)
    ) {
      errors.age = $_("InvalidAge");
      isValid = false;
    }

    if (!agreeToTerms) {
      errors.terms = $_("MustAgreeToTerms");
      isValid = false;
    }

    if (!isValid) {
      return;
    }

    isSubmitting = true;

    try {
      const existingUserCheck = await checkExisting("email", trimmedEmail);
      if (!existingUserCheck) {
        errors.email = $_("EmailAlreadyExists");
        isSubmitting = false;
        return;
      }
      formData.email = trimmedEmail;
      formData.phoneNumber = trimmedPhoneNumber;
      await otpRequest();
    } catch (error: unknown) {
      const message = messageOf(error);
      if (message.includes("email")) {
        errors.email = message;
      } else if (message.includes("phone") || message.includes("msisdn")) {
        errors.phoneNumber = message;
      } else if (message.includes("password")) {
        errors.password = message;
      } else {
        console.error("Registration error:", message);
        showError = true;
      }
    } finally {
      isSubmitting = false;
    }
  }

  async function otpRequest() {
    try {
      await requestOtp(formData.email);
      isOtpStep = true;
      startResendTimer();
    } catch (error: unknown) {
      console.error("OTP request error:", messageOf(error));
      showError = true;
    }
  }

  async function handleOtpVerification() {
    errors.otp = "";

    if (!otpCode.trim()) {
      errors.otp = $_("OtpRequired");
      return;
    }

    if (otpCode.length !== 6) {
      errors.otp = $_("OtpInvalidLength");
      return;
    }

    isVerifyingOtp = true;
    try {
      const defaultRole = await getEntity(
        "web_config",
        "applications",
        "public",
        ResourceType.content,
        getCurrentScope(),
        true,
        false,
      );

      const items: Array<{ key?: string; value?: string }> =
        (defaultRole as { payload?: { body?: { items?: Array<{ key?: string; value?: string }> } } })
          ?.payload?.body?.items ?? [];
      const role = items.find((item) => item.key === "default_user_role")?.value || "catalog_user_role";

      const profileData = {
        gender: formData.gender,
        ...(formData.age && { age: Number(formData.age) }),
        ...(formData.address && { address: formData.address.trim() }),
        ...(formData.confessors.length > 0 && {
          confessors: formData.confessors,
        }),
        ...(formData.profession && { profession: formData.profession.trim() }),
        ...(formData.description && {
          description: formData.description.trim(),
        }),
      };

      await register(
        formData.email,
        otpCode,
        formData.password,
        formData.confirmPassword,
        role,
        profileData,
      );
      goto("/dashboard");
    } catch (error: unknown) {
      const message = messageOf(error);
      console.error("OTP verification error:", message);
      errors.otp = message || $_("OtpVerificationFailed");
    } finally {
      isVerifyingOtp = false;
    }
  }

  async function resendOtp() {
    if (!canResendOtp) return;

    try {
      await otpRequest();
      otpCode = "";
      errors.otp = "";
    } catch (error: unknown) {
      console.error("Resend OTP error:", messageOf(error));
      showError = true;
    }
  }

  function startResendTimer() {
    canResendOtp = false;
    resendCountdown = 60;

    clearInterval(resendTimer);
    resendTimer = setInterval(() => {
      resendCountdown--;
      if (resendCountdown <= 0) {
        canResendOtp = true;
        clearInterval(resendTimer);
      }
    }, 1000);
  }

  function goBackToForm() {
    isOtpStep = false;
    otpCode = "";
    errors.otp = "";
    if (resendTimer) {
      clearInterval(resendTimer);
    }
  }

  function removeConfessor(index: number) {
    const next = formData.confessors.filter((_c, i) => i !== index);
    formData.confessorsText = next.join(", ");
  }

  onDestroy(() => {
    if (resendTimer) {
      clearInterval(resendTimer);
    }
  });
</script>

<div class="register-container">
  <div class="register-content">
    <div class="register-header">
      <div class="icon-wrapper" aria-hidden="true">
        {#if isOtpStep}
          <LockSolid class="header-icon" />
        {:else}
          <UserSolid class="header-icon" />
        {/if}
      </div>
      <h1 class="register-title">
        {isOtpStep ? $_("VerifyEmail") : $_("CreateAccount")}
      </h1>
      <p class="register-description">
        {isOtpStep
          ? `${$_("EnterOtpSentTo")} ${formData.email}`
          : $_("CreateAccountDescription")}
      </p>
    </div>

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
        <p class="error-text">{$_("RegistrationError")}</p>
      </div>
    {/if}

    <div class="form-container">
      <form onsubmit={handleSubmit} class="register-form" novalidate>
        {#if !isOtpStep}
          <!-- Required Fields Section -->
          <fieldset class="form-section">
            <legend class="section-title">
              <UserSolid class="section-icon" aria-hidden="true" />
              {$_("RequiredInformation")}
            </legend>

            <div class="form-group">
              <label for="email" class="form-label">
                <EnvelopeSolid class="label-icon" aria-hidden="true" />
                {$_("Email")}
              </label>
              <input
                id="email"
                type="email"
                bind:value={formData.email}
                placeholder={$_("EmailPlaceholder")}
                class="form-input"
                class:error={errors.email}
                disabled={isSubmitting}
                autocomplete="email"
                inputmode="email"
                aria-invalid={!!errors.email}
                aria-describedby={errors.email ? "email-error" : undefined}
              />
              {#if errors.email}
                <p id="email-error" class="error-text-small" role="alert">{errors.email}</p>
              {/if}
            </div>

            <div class="form-group">
              <label for="phoneNumber" class="form-label">
                <PhoneSolid class="label-icon" aria-hidden="true" />
                {$_("PhoneNumber")}
              </label>
              <input
                id="phoneNumber"
                type="tel"
                bind:value={formData.phoneNumber}
                placeholder={$_("PhoneNumberPlaceholder")}
                class="form-input"
                class:error={errors.phoneNumber}
                disabled={isSubmitting}
                autocomplete="tel"
                aria-invalid={!!errors.phoneNumber}
                aria-describedby={errors.phoneNumber ? "phone-error" : undefined}
              />
              {#if errors.phoneNumber}
                <p id="phone-error" class="error-text-small" role="alert">{errors.phoneNumber}</p>
              {/if}
            </div>

            <div class="form-group">
              <label for="gender" class="form-label">
                <svg class="label-icon" fill="currentColor" viewBox="0 0 20 20" aria-hidden="true">
                  <path
                    fill-rule="evenodd"
                    d="M10 9a3 3 0 100-6 3 3 0 000 6zm-7 9a7 7 0 1114 0H3z"
                    clip-rule="evenodd"
                  />
                </svg>
                {$_("Gender")}
              </label>
              <select
                id="gender"
                bind:value={formData.gender}
                class="form-input"
                class:error={errors.gender}
                disabled={isSubmitting}
                aria-invalid={!!errors.gender}
                aria-describedby={errors.gender ? "gender-error" : undefined}
              >
                <option value="">{$_("SelectGender")}</option>
                <option value="male">{$_("Male")}</option>
                <option value="female">{$_("Female")}</option>
              </select>
              {#if errors.gender}
                <p id="gender-error" class="error-text-small" role="alert">{errors.gender}</p>
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
                  bind:value={formData.password}
                  placeholder={$_("Password")}
                  class="form-input password-input"
                  class:error={errors.password}
                  disabled={isSubmitting}
                  autocomplete="new-password"
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
                <p id="password-error" class="error-text-small" role="alert">{errors.password}</p>
              {/if}
            </div>

            <div class="form-group">
              <label for="confirmPassword" class="form-label">
                <LockSolid class="label-icon" aria-hidden="true" />
                {$_("ConfirmPassword")}
              </label>
              <div class="password-input-wrapper">
                <input
                  id="confirmPassword"
                  type={showConfirmPassword ? "text" : "password"}
                  bind:value={formData.confirmPassword}
                  placeholder={$_("ConfirmPasswordPlaceholder")}
                  class="form-input password-input"
                  class:error={errors.confirmPassword}
                  disabled={isSubmitting}
                  autocomplete="new-password"
                  aria-invalid={!!errors.confirmPassword}
                  aria-describedby={errors.confirmPassword ? "confirm-error" : undefined}
                />
                <button
                  aria-label={$_("ToggleConfirmPasswordVisibility")}
                  aria-pressed={showConfirmPassword}
                  type="button"
                  class="password-toggle"
                  onclick={() => (showConfirmPassword = !showConfirmPassword)}
                >
                  {#if showConfirmPassword}
                    <EyeSlashSolid class="toggle-icon" aria-hidden="true" />
                  {:else}
                    <EyeSolid class="toggle-icon" aria-hidden="true" />
                  {/if}
                </button>
              </div>
              {#if errors.confirmPassword}
                <p id="confirm-error" class="error-text-small" role="alert">{errors.confirmPassword}</p>
              {/if}
            </div>
          </fieldset>

          <!-- Optional Fields Section -->
          <div class="form-section">
            <button
              type="button"
              class="expand-toggle"
              onclick={() => (showAdditionalFields = !showAdditionalFields)}
              aria-expanded={showAdditionalFields}
              aria-controls="additional-fields"
            >
              <svg
                class="expand-icon {showAdditionalFields ? 'expanded' : ''}"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
              </svg>
              <span class="expand-text">
                {showAdditionalFields ? $_("HideAdditionalInformation") : $_("AddAdditionalInformation")}
              </span>
              <span class="optional-badge">{$_("Optional")}</span>
            </button>

            {#if showAdditionalFields}
              <div class="additional-fields" id="additional-fields">
                <p class="additional-fields-description">
                  {$_("CompleteProfileDescription")}
                </p>

                <div class="optional-fields-grid">
                  <div class="form-group">
                    <label for="age" class="form-label">{$_("Age")}</label>
                    <input
                      id="age"
                      type="number"
                      bind:value={formData.age}
                      placeholder={$_("AgePlaceholder")}
                      class="form-input"
                      class:error={errors.age}
                      disabled={isSubmitting}
                      min="1"
                      max="150"
                      aria-invalid={!!errors.age}
                      aria-describedby={errors.age ? "age-error" : undefined}
                    />
                    {#if errors.age}
                      <p id="age-error" class="error-text-small" role="alert">{errors.age}</p>
                    {/if}
                  </div>

                  <div class="form-group">
                    <label for="profession" class="form-label">{$_("Profession")}</label>
                    <input
                      id="profession"
                      type="text"
                      bind:value={formData.profession}
                      placeholder={$_("ProfessionPlaceholder")}
                      class="form-input"
                      disabled={isSubmitting}
                      autocomplete="organization-title"
                    />
                  </div>

                  <div class="form-group full-width">
                    <label for="description" class="form-label">{$_("BioDescription")}</label>
                    <textarea
                      id="description"
                      bind:value={formData.description}
                      placeholder={$_("BioDescriptionPlaceholder")}
                      class="form-textarea"
                      disabled={isSubmitting}
                      rows="4"
                    ></textarea>
                  </div>

                  <div class="form-group full-width">
                    <label for="address" class="form-label">{$_("Address")}</label>
                    <textarea
                      id="address"
                      bind:value={formData.address}
                      placeholder={$_("AddressPlaceholder")}
                      class="form-textarea"
                      disabled={isSubmitting}
                      rows="3"
                      autocomplete="street-address"
                    ></textarea>
                  </div>

                  <div class="form-group full-width">
                    <label for="confessors" class="form-label">
                      {$_("Confessors")}
                      <span class="field-hint">
                        ({formData.confessors.length}
                        {formData.confessors.length === 1 ? $_("ConfessorSingular") : $_("ConfessorPlural")})
                      </span>
                    </label>
                    <textarea
                      id="confessors"
                      bind:value={formData.confessorsText}
                      placeholder={$_("ConfessorsPlaceholder")}
                      class="form-textarea"
                      disabled={isSubmitting}
                      rows="4"
                      aria-describedby="confessors-help"
                    ></textarea>
                    {#if formData.confessors.length > 0}
                      <div class="confessors-preview">
                        <p class="preview-title">{$_("ConfessorsList")}:</p>
                        <ul class="confessors-tags">
                          {#each formData.confessors as confessor, index (`${index}:${confessor}`)}
                            <li class="confessor-tag">
                              {confessor}
                              <button
                                type="button"
                                class="remove-tag"
                                aria-label="{$_('ui.remove')} {confessor}"
                                onclick={() => removeConfessor(index)}
                                disabled={isSubmitting}
                              >
                                ×
                              </button>
                            </li>
                          {/each}
                        </ul>
                      </div>
                    {/if}
                    <p class="field-help-text" id="confessors-help">
                      {$_("ConfessorsHelpText")}
                    </p>
                  </div>
                </div>
              </div>
            {/if}
          </div>

          <!-- Terms and Conditions -->
          <div class="form-group">
            <label for="agreeToTerms" class="checkbox-label">
              <input
                id="agreeToTerms"
                type="checkbox"
                bind:checked={agreeToTerms}
                class="checkbox-input"
                disabled={isSubmitting}
                aria-invalid={!!errors.terms}
                aria-describedby={errors.terms ? "terms-error" : undefined}
              />
              <span class="checkbox-text">{$_("AgreeToTerms")}</span>
            </label>
            {#if errors.terms}
              <p id="terms-error" class="error-text-small" role="alert">{errors.terms}</p>
            {/if}
          </div>
        {:else}
          <!-- OTP Verification -->
          <div class="form-group">
            <label for="otpCode" class="form-label">
              <LockSolid class="label-icon" aria-hidden="true" />
              {$_("VerificationCode")}
            </label>
            <input
              id="otpCode"
              type="text"
              inputmode="numeric"
              autocomplete="one-time-code"
              bind:value={otpCode}
              placeholder={$_("EnterOtpCode")}
              class="form-input otp-input"
              class:error={errors.otp}
              disabled={isVerifyingOtp}
              maxlength="6"
              aria-invalid={!!errors.otp}
              aria-describedby={errors.otp ? "otp-error" : undefined}
            />
            {#if errors.otp}
              <p id="otp-error" class="error-text-small" role="alert">{errors.otp}</p>
            {/if}
          </div>

          <div class="resend-otp-container">
            <p class="resend-text">{$_("DidNotReceiveOtp")}</p>
            <button type="button" class="resend-button" onclick={resendOtp} disabled={!canResendOtp}>
              {#if canResendOtp}
                {$_("ResendOtp")}
              {:else}
                {$_("ResendIn")} {resendCountdown}s
              {/if}
            </button>
          </div>
        {/if}

        <button
          type="submit"
          class="submit-button"
          disabled={isSubmitting || isVerifyingOtp}
          aria-busy={isSubmitting || isVerifyingOtp}
        >
          {#if isSubmitting || isVerifyingOtp}
            <span class="spinner spinner-xs spinner-white" aria-hidden="true"></span>
            {isOtpStep ? $_("VerifyingOtp") : $_("SigningUp")}
          {:else if isOtpStep}
            <LockSolid class="button-icon" aria-hidden="true" />
            {$_("VerifyOtp")}
          {:else}
            <UserSolid class="button-icon" aria-hidden="true" />
            {$_("SendOtp")}
          {/if}
        </button>
      </form>

      {#if isOtpStep}
        <div class="back-link">
          <button type="button" class="link-button" onclick={goBackToForm}>
            <ArrowLeftOutline class="back-icon rtl:rotate-180" aria-hidden="true" />
            {$_("BackToForm")}
          </button>
        </div>
      {:else}
        <div class="login-link">
          <span class="login-text">{$_("AlreadyHaveAccount")}</span>
          <a class="link-button" href={withBase("/login")}>{$_("SignIn")}</a>
        </div>
      {/if}
    </div>
  </div>
</div>

<style>
  .register-container {
    min-height: 100vh;
    background: var(--gradient-page);
    padding: 2rem var(--space-page-x);
    position: relative;
  }

  .register-container::before {
    content: "";
    position: absolute;
    top: -30%;
    inset-inline-end: -15%;
    width: 50%;
    height: 60%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.06) 0%, transparent 70%);
    pointer-events: none;
  }

  .register-content {
    max-width: 720px;
    margin: 0 auto;
    position: relative;
    z-index: 1;
    animation: fadeInUp var(--duration-slow) var(--ease-out);
  }

  .register-header {
    text-align: center;
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

  .icon-wrapper :global(.header-icon) {
    width: 1.5rem;
    height: 1.5rem;
  }

  .register-title {
    font-size: 1.75rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.5rem;
    letter-spacing: -0.02em;
  }

  .register-description {
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
  }

  .register-form {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .form-section {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
    border: 0;
    padding: 0;
    margin: 0;
    min-width: 0;
  }

  .section-title {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-weight: 600;
    color: var(--color-text);
    font-size: 0.9375rem;
    margin-bottom: 0.25rem;
    padding: 0 0 0.5rem;
    border-bottom: 1.5px solid var(--color-border);
    width: 100%;
  }

  .section-title :global(.section-icon) {
    width: 1rem;
    height: 1rem;
    color: var(--color-primary);
  }

  .expand-toggle {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    width: 100%;
    background: var(--color-surface);
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    padding: 0.75rem 1rem;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    font-weight: 500;
    font-size: 0.875rem;
    color: var(--color-text);
  }

  .expand-toggle:hover {
    background: var(--color-surface-2);
    border-color: var(--color-border-strong);
    box-shadow: var(--shadow-sm);
  }

  .expand-icon {
    width: 1.125rem;
    height: 1.125rem;
    transition: transform var(--duration-normal) var(--ease-out);
    color: var(--color-text-faint);
    flex-shrink: 0;
  }

  .expand-icon.expanded {
    transform: rotate(180deg);
  }

  .expand-text {
    flex: 1;
    text-align: start;
  }

  .optional-badge {
    background: var(--color-primary-soft);
    color: var(--color-primary);
    padding: 0.125rem 0.625rem;
    border-radius: var(--radius-full);
    font-size: 0.6875rem;
    font-weight: 600;
    letter-spacing: 0.02em;
  }

  .additional-fields {
    background: var(--color-surface);
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-card);
    padding: 1.25rem;
    margin-top: 0.75rem;
    animation: fadeInUp var(--duration-normal) var(--ease-out);
  }

  .additional-fields-description {
    color: var(--color-text-muted);
    font-size: 0.8125rem;
    margin-bottom: 1.25rem;
    text-align: center;
  }

  .optional-fields-grid {
    display: grid;
    grid-template-columns: 1fr;
    gap: 1.25rem;
  }

  @media (min-width: 640px) {
    .optional-fields-grid {
      grid-template-columns: 1fr 1fr;
    }
  }

  .optional-fields-grid .form-group.full-width {
    grid-column: 1 / -1;
  }

  .form-group {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
    min-width: 0;
  }

  .form-label {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    font-weight: 500;
    color: var(--color-text);
    font-size: 0.8125rem;
  }

  .form-label :global(.label-icon),
  .label-icon {
    width: 0.875rem;
    height: 0.875rem;
    color: var(--color-text-faint);
  }

  .field-hint {
    font-size: 0.6875rem;
    color: var(--color-text-faint);
    font-weight: 400;
    margin-inline-start: 0.25rem;
  }

  .form-input,
  .form-textarea {
    padding: 0.6875rem 0.875rem;
    border: 1.5px solid var(--color-border);
    border-radius: var(--radius-control);
    font-size: 0.9375rem;
    transition: all var(--duration-normal) var(--ease-out);
    background: var(--color-surface);
    color: var(--color-text);
    width: 100%;
  }

  .form-input::placeholder,
  .form-textarea::placeholder {
    color: var(--color-text-faint);
  }

  .form-input:hover,
  .form-textarea:hover {
    border-color: var(--color-border-strong);
  }

  .form-input:focus,
  .form-textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px var(--color-primary-soft);
    background: var(--color-surface-2);
  }

  .form-input.error {
    border-color: var(--color-danger);
    box-shadow: 0 0 0 3px var(--color-danger-soft);
  }

  .form-textarea {
    resize: vertical;
    min-height: 80px;
    font-family: inherit;
  }

  .otp-input {
    letter-spacing: 0.25em;
    font-variant-numeric: tabular-nums;
  }

  .confessors-preview {
    margin-top: 0.5rem;
    padding: 0.75rem;
    background: var(--color-surface-2);
    border-radius: var(--radius-control);
    border: 1px solid var(--color-border);
  }

  .preview-title {
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-text-muted);
    margin-bottom: 0.5rem;
  }

  .confessors-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 0.375rem;
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .confessor-tag {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    background: var(--color-primary-soft);
    color: var(--color-primary);
    padding: 0.25rem 0.5rem;
    border-radius: var(--radius-full);
    font-size: 0.75rem;
    font-weight: 500;
  }

  .remove-tag {
    background: none;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    font-weight: bold;
    font-size: 0.875rem;
    line-height: 1;
    padding: 0;
    margin-inline-start: 0.125rem;
    width: 1rem;
    height: 1rem;
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 50%;
    transition: all var(--duration-fast) ease;
  }

  .remove-tag:hover:not(:disabled) {
    background: var(--color-primary-200);
  }

  .remove-tag:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .field-help-text {
    font-size: 0.6875rem;
    color: var(--color-text-faint);
    margin-top: 0.25rem;
  }

  .password-input-wrapper {
    position: relative;
    display: flex;
    align-items: center;
  }

  .password-input {
    padding-inline-end: 2.75rem;
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

  .checkbox-label {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    cursor: pointer;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }

  .checkbox-input {
    width: 1rem;
    height: 1rem;
    accent-color: var(--color-primary);
    border-radius: var(--radius-control);
    flex-shrink: 0;
  }

  .checkbox-text {
    line-height: 1.4;
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
  }

  .submit-button:disabled {
    opacity: 0.6;
    cursor: not-allowed;
    transform: none;
  }

  .login-link,
  .back-link {
    display: flex;
    justify-content: center;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.25rem;
    margin-top: 1.5rem;
    padding-top: 1.25rem;
    border-top: 1px solid var(--color-border);
  }

  .login-text {
    color: var(--color-text-muted);
    font-size: 0.8125rem;
  }

  .link-button {
    display: inline-flex;
    align-items: center;
    gap: 0.375rem;
    background: none;
    border: none;
    color: var(--color-primary);
    font-weight: 600;
    cursor: pointer;
    text-decoration: none;
    font-size: 0.8125rem;
    transition: color var(--duration-fast) ease;
  }

  .link-button :global(.back-icon) {
    width: 1rem;
    height: 1rem;
  }

  .link-button:hover {
    color: var(--color-primary-hover);
    text-decoration: underline;
  }

  .resend-otp-container {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 0.5rem;
    font-size: 0.8125rem;
  }

  .resend-text {
    color: var(--color-text-muted);
  }

  .resend-button {
    background: none;
    border: none;
    color: var(--color-primary);
    font-weight: 600;
    cursor: pointer;
    text-decoration: none;
    font-size: 0.8125rem;
    font-variant-numeric: tabular-nums;
    transition: color var(--duration-fast) ease;
  }

  .resend-button:disabled {
    color: var(--color-text-faint);
    cursor: not-allowed;
  }

  .resend-button:hover:not(:disabled) {
    color: var(--color-primary-hover);
    text-decoration: underline;
  }

  @media (max-width: 640px) {
    .register-container {
      padding: 1rem var(--space-page-x);
    }
    .register-title {
      font-size: 1.5rem;
    }
    .form-container {
      padding: 1.5rem;
      border-radius: var(--radius-card);
    }
    .additional-fields {
      padding: 1rem;
    }
    .optional-fields-grid {
      grid-template-columns: 1fr;
    }
  }
</style>
