<script lang="ts">
  import { EyeSlashSolid, LockSolid, ShieldCheckSolid, UsersSolid } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // $derived so a locale switch re-renders the labels (review #28).
  const principles = $derived([
    { icon: ShieldCheckSolid, title: $_("privacy.principles.data_protection.title"), description: $_("privacy.principles.data_protection.description") },
    { icon: EyeSlashSolid, title: $_("privacy.principles.minimal_collection.title"), description: $_("privacy.principles.minimal_collection.description") },
    { icon: LockSolid, title: $_("privacy.principles.secure_storage.title"), description: $_("privacy.principles.secure_storage.description") },
    { icon: UsersSolid, title: $_("privacy.principles.user_control.title"), description: $_("privacy.principles.user_control.description") },
  ]);

  const sections = $derived([
    {
      title: $_("privacy.information_collect.title"),
      items: [
        $_("privacy.information_collect.account_info"),
        $_("privacy.information_collect.content"),
        $_("privacy.information_collect.usage_data"),
        $_("privacy.information_collect.communication"),
      ],
    },
    {
      title: $_("privacy.how_we_use.title"),
      items: [
        $_("privacy.how_we_use.provide_services"),
        $_("privacy.how_we_use.personalize"),
        $_("privacy.how_we_use.communicate"),
        $_("privacy.how_we_use.improve"),
      ],
    },
    {
      title: $_("privacy.data_sharing.title"),
      description: $_("privacy.data_sharing.description"),
      items: [
        $_("privacy.data_sharing.consent"),
        $_("privacy.data_sharing.legal"),
        $_("privacy.data_sharing.protect_rights"),
        $_("privacy.data_sharing.service_providers"),
      ],
    },
    {
      title: $_("privacy.your_rights.title"),
      items: [
        $_("privacy.your_rights.access"),
        $_("privacy.your_rights.correct"),
        $_("privacy.your_rights.delete"),
        $_("privacy.your_rights.export"),
        $_("privacy.your_rights.opt_out"),
      ],
    },
  ]);

  $effect(() => setTitle($_("Privacy")));
</script>

<div class="privacy-container">
  <section class="hero-section">
    <div class="hero-content">
      <div class="hero-text">
        <h1 class="hero-title">
          <span class="gradient-text">{$_("privacy.hero.title")}</span>
          {$_("privacy.hero.policy")}
        </h1>
        <p class="hero-description">
          {$_("privacy.hero.description")}
        </p>
      </div>
    </div>
  </section>

  <section class="content-section">
    <div class="content-wrapper">
      <div class="privacy-principles">
        <h2 class="section-title">{$_("privacy.principles.title")}</h2>
        <div class="principles-grid">
          {#each principles as principle (principle.title)}
            <div class="principle-card">
              <div class="principle-icon" aria-hidden="true">
                <principle.icon class="icon" color="currentColor" />
              </div>
              <h3>{principle.title}</h3>
              <p>{principle.description}</p>
            </div>
          {/each}
        </div>
      </div>

      <div class="privacy-details">
        {#each sections as section (section.title)}
          <div class="detail-section">
            <h3>{section.title}</h3>
            {#if section.description}
              <p>{section.description}</p>
            {/if}
            <ul>
              {#each section.items as item (item)}
                <li>{item}</li>
              {/each}
            </ul>
          </div>
        {/each}

        <div class="contact-section">
          <h3>{$_("privacy.questions.title")}</h3>
          <p>
            {$_("privacy.questions.description")}
          </p>
          <a class="btn-contact" href={withBase("/contact")}>{$_("privacy.questions.button")}</a>
        </div>
      </div>
    </div>
  </section>
</div>

<style>
  .privacy-container {
    min-height: 100vh;
    background: var(--color-surface);
  }

  .hero-section {
    padding: clamp(3rem, 6vw, 5rem) 0 clamp(2rem, 4vw, 3rem) 0;
    background: var(--gradient-page);
  }

  .hero-content { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); }
  .hero-text { text-align: center; }

  .hero-title {
    font-size: clamp(2rem, 4vw, 3rem);
    font-weight: 800;
    color: var(--color-text);
    margin-bottom: 1rem;
    line-height: 1.1;
    letter-spacing: -0.02em;
  }

  .gradient-text {
    background: var(--gradient-brand);
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
  }

  .hero-description { font-size: 1.0625rem; color: var(--color-text-muted); max-width: 40rem; margin: 0 auto; line-height: 1.6; }

  .content-section { padding: var(--space-section-y) 0; background: var(--color-surface-2); }
  .content-wrapper { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); }

  .section-title { font-size: 1.75rem; font-weight: 800; color: var(--color-text); text-align: center; margin-bottom: 2.5rem; letter-spacing: -0.02em; }

  .principles-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 1.5rem; margin-bottom: 3rem; }

  .principle-card {
    background: var(--color-surface-2);
    padding: 1.75rem;
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    text-align: center;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .principle-card:hover { transform: translateY(-4px); box-shadow: var(--shadow-lg); border-color: var(--color-primary-300); }

  .principle-icon {
    width: 2.75rem;
    height: 2.75rem;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    border-radius: var(--radius-control);
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.25rem auto;
    box-shadow: var(--shadow-brand);
  }

  .principle-card h3 { font-size: 1.0625rem; font-weight: 700; color: var(--color-text); margin-bottom: 0.5rem; }
  .principle-card p { color: var(--color-text-muted); line-height: 1.6; font-size: 0.9375rem; }

  .privacy-details { max-width: 720px; margin: 0 auto; }

  .detail-section {
    margin-bottom: 2rem;
    padding: 1.75rem;
    background: var(--color-surface);
    border-radius: var(--radius-card);
    border: 1px solid var(--color-border);
  }

  .detail-section h3 { font-size: 1.25rem; font-weight: 700; color: var(--color-text); margin-bottom: 0.75rem; }
  .detail-section p { color: var(--color-text-muted); line-height: 1.6; margin-bottom: 0.75rem; font-size: 0.9375rem; }
  .detail-section ul { list-style: none; padding: 0; margin: 0; }
  .detail-section li {
    color: var(--color-text-muted);
    line-height: 1.6;
    margin-bottom: 0.375rem;
    padding-inline-start: 1.25rem;
    position: relative;
    font-size: 0.9375rem;
  }
  .detail-section li::before {
    content: "";
    position: absolute;
    inset-inline-start: 0.25rem;
    top: 0.65em;
    width: 0.375rem;
    height: 0.375rem;
    border-radius: 50%;
    background: var(--color-primary);
  }

  .contact-section {
    text-align: center;
    padding: 2.5rem 2rem;
    background: var(--gradient-brand);
    border-radius: var(--radius-modal);
    color: var(--color-text-on-primary);
    position: relative;
    overflow: hidden;
  }

  .contact-section::before {
    content: "";
    position: absolute;
    top: -40%;
    inset-inline-end: -25%;
    width: 50%;
    height: 80%;
    background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, transparent 60%);
    pointer-events: none;
  }

  .contact-section h3 { color: inherit; margin-bottom: 0.75rem; position: relative; font-size: 1.25rem; font-weight: 700; }
  .contact-section p { color: rgba(255,255,255,0.85); margin-bottom: 1.5rem; position: relative; font-size: 0.9375rem; line-height: 1.6; }

  .btn-contact {
    display: inline-flex;
    background: var(--color-surface-2);
    color: var(--color-primary);
    font-weight: 600;
    padding: 0.625rem 1.5rem;
    border-radius: var(--radius-control);
    border: none;
    cursor: pointer;
    text-decoration: none;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem;
    position: relative;
  }

  .btn-contact:hover { background: var(--color-surface-3); transform: translateY(-1px); box-shadow: 0 4px 12px rgba(0,0,0,0.1); }

  @media (max-width: 640px) {
    .hero-section { padding: 2.5rem 0 1.5rem 0; }
    .content-section { padding: 2.5rem 0; }
    .detail-section { padding: 1.25rem; }
  }
</style>
