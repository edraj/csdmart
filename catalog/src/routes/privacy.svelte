<script>
    import {goto} from "@roxi/routify";
    import {EyeSlashSolid, LockSolid, ShieldCheckSolid, UsersSolid,} from "flowbite-svelte-icons";
    import {_} from "@/i18n";

    $goto;

  function handleContactUs() {
    $goto("/contact");
  }
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
          <div class="principle-card">
            <div class="principle-icon">
              <ShieldCheckSolid class="icon" color="white" />
            </div>
            <h3>{$_("privacy.principles.data_protection.title")}</h3>
            <p>
              {$_("privacy.principles.data_protection.description")}
            </p>
          </div>
          <div class="principle-card">
            <div class="principle-icon">
              <EyeSlashSolid class="icon" color="white" />
            </div>
            <h3>{$_("privacy.principles.minimal_collection.title")}</h3>
            <p>
              {$_("privacy.principles.minimal_collection.description")}
            </p>
          </div>
          <div class="principle-card">
            <div class="principle-icon">
              <LockSolid class="icon" color="white" />
            </div>
            <h3>{$_("privacy.principles.secure_storage.title")}</h3>
            <p>
              {$_("privacy.principles.secure_storage.description")}
            </p>
          </div>
          <div class="principle-card">
            <div class="principle-icon">
              <UsersSolid class="icon" color="white" />
            </div>
            <h3>{$_("privacy.principles.user_control.title")}</h3>
            <p>
              {$_("privacy.principles.user_control.description")}
            </p>
          </div>
        </div>
      </div>

      <div class="privacy-details">
        <div class="detail-section">
          <h3>{$_("privacy.information_collect.title")}</h3>
          <ul>
            <li>{$_("privacy.information_collect.account_info")}</li>
            <li>{$_("privacy.information_collect.content")}</li>
            <li>{$_("privacy.information_collect.usage_data")}</li>
            <li>{$_("privacy.information_collect.communication")}</li>
          </ul>
        </div>

        <div class="detail-section">
          <h3>{$_("privacy.how_we_use.title")}</h3>
          <ul>
            <li>{$_("privacy.how_we_use.provide_services")}</li>
            <li>{$_("privacy.how_we_use.personalize")}</li>
            <li>{$_("privacy.how_we_use.communicate")}</li>
            <li>{$_("privacy.how_we_use.improve")}</li>
          </ul>
        </div>

        <div class="detail-section">
          <h3>{$_("privacy.data_sharing.title")}</h3>
          <p>
            {$_("privacy.data_sharing.description")}
          </p>
          <ul>
            <li>{$_("privacy.data_sharing.consent")}</li>
            <li>{$_("privacy.data_sharing.legal")}</li>
            <li>{$_("privacy.data_sharing.protect_rights")}</li>
            <li>{$_("privacy.data_sharing.service_providers")}</li>
          </ul>
        </div>

        <div class="detail-section">
          <h3>{$_("privacy.your_rights.title")}</h3>
          <ul>
            <li>{$_("privacy.your_rights.access")}</li>
            <li>{$_("privacy.your_rights.correct")}</li>
            <li>{$_("privacy.your_rights.delete")}</li>
            <li>{$_("privacy.your_rights.export")}</li>
            <li>{$_("privacy.your_rights.opt_out")}</li>
          </ul>
        </div>

        <div class="contact-section">
          <h3>{$_("privacy.questions.title")}</h3>
          <p>
            {$_("privacy.questions.description")}
          </p>
          <button
            aria-label={`Contact us`}
            class="btn-contact"
            onclick={handleContactUs}
          >
            {$_("privacy.questions.button")}
          </button>
        </div>
      </div>
    </div>
  </section>
</div>

<style>
  .privacy-container {
    min-height: 100vh;
    background: var(--surface-page);
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
    color: var(--color-gray-900);
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

  .hero-description { font-size: 1.0625rem; color: var(--color-gray-500); max-width: 40rem; margin: 0 auto; line-height: 1.6; }

  .content-section { padding: var(--space-section-y) 0; background: white; }
  .content-wrapper { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); }

  .section-title { font-size: 1.75rem; font-weight: 800; color: var(--color-gray-900); text-align: center; margin-bottom: 2.5rem; letter-spacing: -0.02em; }

  .principles-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 1.5rem; margin-bottom: 3rem; }

  .principle-card {
    background: white; padding: 1.75rem; border-radius: var(--radius-xl); box-shadow: var(--shadow-sm);
    border: 1px solid var(--color-gray-100); text-align: center; transition: all var(--duration-normal) var(--ease-out);
  }

  .principle-card:hover { transform: translateY(-4px); box-shadow: var(--shadow-xl); border-color: var(--color-primary-100); }

  .principle-icon {
    width: 2.75rem; height: 2.75rem; background: var(--gradient-brand); border-radius: var(--radius-lg);
    display: flex; align-items: center; justify-content: center; margin: 0 auto 1.25rem auto; box-shadow: var(--shadow-brand);
  }

  .principle-card h3 { font-size: 1.0625rem; font-weight: 700; color: var(--color-gray-800); margin-bottom: 0.5rem; }
  .principle-card p { color: var(--color-gray-500); line-height: 1.6; font-size: 0.9375rem; }

  .privacy-details { max-width: 720px; margin: 0 auto; }

  .detail-section {
    margin-bottom: 2rem; padding: 1.75rem; background: var(--color-gray-50); border-radius: var(--radius-xl);
    border: 1px solid var(--color-gray-200);
  }

  .detail-section h3 { font-size: 1.25rem; font-weight: 700; color: var(--color-gray-800); margin-bottom: 0.75rem; }
  .detail-section p { color: var(--color-gray-500); line-height: 1.6; margin-bottom: 0.75rem; font-size: 0.9375rem; }
  .detail-section ul { list-style: none; padding: 0; margin: 0; }
  .detail-section li { color: var(--color-gray-500); line-height: 1.6; margin-bottom: 0.375rem; padding-left: 1.25rem; position: relative; font-size: 0.9375rem; }

  .contact-section {
    text-align: center; padding: 2.5rem 2rem; background: var(--gradient-brand); border-radius: var(--radius-2xl);
    color: white; position: relative; overflow: hidden;
  }

  .contact-section::before {
    content: ""; position: absolute; top: -40%; right: -25%; width: 50%; height: 80%;
    background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, transparent 60%); pointer-events: none;
  }

  .contact-section h3 { color: white !important; margin-bottom: 0.75rem; position: relative; }
  .contact-section p { color: rgba(255,255,255,0.85) !important; margin-bottom: 1.5rem; position: relative; font-size: 0.9375rem; }

  .btn-contact {
    background: white; color: var(--color-primary-600); font-weight: 600; padding: 0.625rem 1.5rem;
    border-radius: var(--radius-lg); border: none; cursor: pointer; transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem; position: relative;
  }

  .btn-contact:hover { background: var(--color-gray-50); transform: translateY(-1px); box-shadow: 0 4px 12px rgba(0,0,0,0.1); }

  @media (max-width: 640px) {
    .hero-section { padding: 2.5rem 0 1.5rem 0; }
    .content-section { padding: 2.5rem 0; }
    .detail-section { padding: 1.25rem; }
  }
</style>
