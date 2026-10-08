<script lang="ts">
  import {
    BellSolid,
    EditSolid,
    EyeSolid,
    HeartSolid,
    MessageCaptionSolid,
    MessagesOutline,
    UserSolid,
  } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // Labels are $derived so a locale switch re-renders them; built once at
  // init they stayed in the first language (review #28).
  const features = $derived([
    { icon: HeartSolid, title: $_("InteractiveEngagement"), description: $_("InteractiveEngagementDesc") },
    { icon: BellSolid, title: $_("RealtimeNotifications"), description: $_("RealtimeNotificationsDesc") },
    { icon: UserSolid, title: $_("PersonalProfile"), description: $_("PersonalProfileDesc") },
    { icon: EyeSolid, title: $_("PublicBrowseMode"), description: $_("PublicBrowseModeDesc") },
    { icon: EditSolid, title: $_("ContentManagement"), description: $_("ContentManagementDesc") },
    { icon: MessageCaptionSolid, title: $_("RichDiscussions"), description: $_("RichDiscussionsDesc") },
  ]);

  $effect(() => setTitle($_("nav.home")));
</script>

<div class="home-container">
  <section class="hero-section">
    <div class="hero-content">
      <div class="hero-text">
        <h1 class="hero-title">
          {$_("ShareIdeas")},
          <span class="gradient-text"> {$_("BuildCommunity")} </span>
        </h1>
        <p class="hero-description">
          {$_("HeroDescription")}
        </p>

        <div class="hero-buttons">
          <a class="btn-primary" href={withBase("/dashboard")}>{$_("StartExploring")}</a>
          <a class="btn-secondary" href={withBase("/login")}>{$_("SignIn")}</a>
          <a class="btn-secondary" href={withBase("/register")}>{$_("CreateAccount")}</a>
        </div>
      </div>
    </div>
  </section>

  <section class="features-section">
    <div class="features-content">
      <div class="features-header">
        <h2 class="features-title">{$_("FeaturesTitle")}</h2>
        <p class="features-description">
          {$_("FeaturesDescription")}
        </p>
      </div>

      <div class="features-grid">
        {#each features as feature (feature.title)}
          <div class="feature-card">
            <div class="feature-icon" aria-hidden="true">
              <feature.icon class="icon" color="currentColor" />
            </div>
            <h3 class="feature-title">
              {feature.title}
            </h3>
            <p class="feature-description">
              {feature.description}
            </p>
          </div>
        {/each}
      </div>
    </div>
  </section>

  <section class="cta-section">
    <div class="cta-content">
      <h2 class="cta-title">{$_("CTATitle")}</h2>
      <p class="cta-description">
        {$_("CTADescription")}
      </p>

      <div class="cta-buttons">
        <a class="btn-cta-secondary" href={withBase("/dashboard")}>{$_("ExploreAsGuest")}</a>
        <a class="btn-cta-secondary" href={withBase("/contact")}>
          <MessagesOutline class="button-icon" aria-hidden="true" />
          {$_("ContactUs")}
        </a>
      </div>
    </div>
  </section>

  <footer class="footer-section">
    <div class="footer-content">
      <div class="footer-grid">
        <div class="footer-brand">
          <h3 class="brand-name">{$_("Catalog")}</h3>
          <p class="brand-description">
            {$_("BrandDescription")}
          </p>
        </div>

        <nav class="footer-column" aria-label={$_("Support")}>
          <h4 class="footer-column-title">{$_("Support")}</h4>
          <ul class="footer-links">
            <li><a href={withBase("/help")}>{$_("HelpCenter")}</a></li>
            <li><a href={withBase("/community")}>{$_("Community")}</a></li>
            <li><a href={withBase("/contact")}>{$_("ContactUs")}</a></li>
            <li><a href={withBase("/privacy")}>{$_("Privacy")}</a></li>
          </ul>
        </nav>
      </div>

      <div class="footer-bottom">
        <p>{$_("Copyright")}</p>
      </div>
    </div>
  </footer>
</div>

<style>
  .home-container {
    min-height: 100vh;
    background: var(--color-surface);
  }

  /* ─── Hero ─── */
  .hero-section {
    padding: clamp(4rem, 8vw, 7rem) 0 clamp(5rem, 10vw, 9rem) 0;
    position: relative;
    background: var(--gradient-page);
    overflow: hidden;
  }

  .hero-section::before {
    content: "";
    position: absolute;
    top: -20%;
    inset-inline-end: -10%;
    width: 50%;
    height: 70%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.08) 0%, transparent 65%);
    pointer-events: none;
  }

  .hero-section::after {
    content: "";
    position: absolute;
    bottom: -15%;
    inset-inline-start: -5%;
    width: 40%;
    height: 50%;
    background: radial-gradient(circle, rgba(139, 92, 246, 0.06) 0%, transparent 65%);
    pointer-events: none;
  }

  .hero-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
    position: relative;
    z-index: 1;
  }

  .hero-text {
    text-align: center;
  }

  .hero-title {
    font-size: clamp(2.25rem, 5vw, 3.5rem);
    font-weight: 800;
    color: var(--color-text);
    margin-bottom: 1.25rem;
    line-height: 1.1;
    letter-spacing: -0.03em;
    animation: fadeInUp 0.7s var(--ease-out) both;
  }

  .gradient-text {
    background: var(--gradient-brand);
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    color: transparent;
  }

  .hero-description {
    font-size: clamp(1rem, 2vw, 1.1875rem);
    color: var(--color-text-muted);
    margin-bottom: 2.5rem;
    max-width: 42rem;
    margin-inline: auto;
    line-height: 1.65;
    animation: fadeInUp 0.7s var(--ease-out) 0.15s both;
  }

  .hero-buttons {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    justify-content: center;
    align-items: center;
    margin-bottom: 0;
    animation: fadeInUp 0.7s var(--ease-out) 0.3s both;
  }

  @media (min-width: 640px) {
    .hero-buttons {
      flex-direction: row;
    }
  }

  .btn-primary,
  .btn-secondary,
  .btn-cta-secondary {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    font-weight: 600;
    padding: 0.75rem 1.75rem;
    border-radius: var(--radius-control);
    cursor: pointer;
    text-decoration: none;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem;
  }

  .btn-primary {
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    border: none;
    box-shadow: var(--shadow-brand);
  }

  .btn-primary:hover {
    background: var(--gradient-brand-hover);
    transform: translateY(-2px);
    box-shadow: var(--shadow-brand-lg);
  }

  .btn-primary:active {
    transform: translateY(0);
  }

  .btn-secondary {
    background: var(--color-surface-2);
    color: var(--color-text);
    border: 1.5px solid var(--color-border);
  }

  .btn-secondary:hover {
    background: var(--color-surface-3);
    border-color: var(--color-border-strong);
    transform: translateY(-1px);
    box-shadow: var(--shadow-sm);
  }

  /* ─── Features ─── */
  .features-section {
    padding: var(--space-section-y) 0;
    background: var(--color-surface-2);
  }

  .features-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .features-header {
    text-align: center;
    margin-bottom: 3rem;
  }

  .features-title {
    font-size: clamp(1.75rem, 3.5vw, 2.25rem);
    font-weight: 800;
    color: var(--color-text);
    margin-bottom: 0.75rem;
    letter-spacing: -0.02em;
  }

  .features-description {
    font-size: 1.0625rem;
    color: var(--color-text-muted);
    max-width: 30rem;
    margin: 0 auto;
    line-height: 1.6;
  }

  .features-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
    gap: 1.5rem;
  }

  .feature-card {
    background: var(--color-surface-2);
    padding: 1.75rem;
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    transition:
      transform var(--duration-slow) var(--ease-out),
      box-shadow var(--duration-slow) var(--ease-out),
      border-color var(--duration-slow) var(--ease-out);
  }

  .feature-card:hover {
    transform: translateY(-4px);
    box-shadow: var(--shadow-lg);
    border-color: var(--color-primary-300);
  }

  .feature-icon {
    width: 2.75rem;
    height: 2.75rem;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    border-radius: var(--radius-control);
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 1.25rem;
    box-shadow: var(--shadow-brand);
  }

  .feature-title {
    font-size: 1.0625rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.5rem;
  }

  .feature-description {
    color: var(--color-text-muted);
    line-height: 1.6;
    font-size: 0.9375rem;
  }

  /* ─── CTA ─── */
  .cta-section {
    padding: var(--space-section-y) 0;
    background: var(--gradient-brand);
    position: relative;
    overflow: hidden;
  }

  .cta-section::before {
    content: "";
    position: absolute;
    top: -50%;
    inset-inline-end: -20%;
    width: 60%;
    height: 100%;
    background: radial-gradient(circle, rgba(255, 255, 255, 0.08) 0%, transparent 60%);
    pointer-events: none;
  }

  .cta-content {
    max-width: 48rem;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
    text-align: center;
    position: relative;
    z-index: 1;
  }

  .cta-title {
    font-size: clamp(1.75rem, 3.5vw, 2.25rem);
    font-weight: 800;
    color: var(--color-text-on-primary);
    margin-bottom: 1rem;
    letter-spacing: -0.02em;
  }

  .cta-description {
    font-size: 1.0625rem;
    color: rgba(255, 255, 255, 0.85);
    margin-bottom: 2rem;
    max-width: 30rem;
    margin-inline: auto;
    line-height: 1.6;
  }

  .cta-buttons {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    justify-content: center;
    align-items: center;
  }

  @media (min-width: 640px) {
    .cta-buttons {
      flex-direction: row;
    }
  }

  .btn-cta-secondary {
    background: rgba(255, 255, 255, 0.12);
    color: var(--color-text-on-primary);
    border: 1.5px solid rgba(255, 255, 255, 0.4);
    backdrop-filter: blur(4px);
  }

  .btn-cta-secondary:hover {
    background: var(--color-surface-2);
    color: var(--color-primary);
    border-color: var(--color-surface-2);
    transform: translateY(-1px);
    box-shadow: 0 8px 20px rgba(0, 0, 0, 0.15);
  }

  .btn-cta-secondary :global(.button-icon) {
    width: 1.125rem;
    height: 1.125rem;
  }

  /* ─── Footer ─── */
  .footer-section {
    background: var(--color-gray-900);
    color: var(--color-gray-50);
  }

  .footer-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .footer-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 2rem;
    padding: 3rem 0 2.5rem;
  }

  .footer-brand {
    grid-column: span 2;
  }

  @media (max-width: 768px) {
    .footer-brand {
      grid-column: span 1;
    }
  }

  .brand-name {
    font-size: 1.375rem;
    font-weight: 800;
    background: linear-gradient(135deg, var(--color-primary-300) 0%, var(--color-accent-400) 100%);
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
    color: transparent;
    margin-bottom: 0.75rem;
  }

  .brand-description {
    color: var(--color-gray-400);
    margin-bottom: 1.5rem;
    max-width: 22rem;
    line-height: 1.6;
    font-size: 0.9375rem;
  }

  .footer-column-title {
    font-weight: 600;
    font-size: 0.875rem;
    color: var(--color-gray-300);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-bottom: 1rem;
  }

  .footer-links {
    list-style: none;
    padding: 0;
    margin: 0;
  }

  .footer-links li {
    margin-bottom: 0.5rem;
  }

  .footer-links a {
    color: var(--color-gray-400);
    text-decoration: none;
    transition: color var(--duration-fast) ease;
    font-size: 0.9375rem;
  }

  .footer-links a:hover {
    color: var(--color-gray-50);
  }

  .footer-bottom {
    border-top: 1px solid rgba(255, 255, 255, 0.08);
    padding: 1.5rem 0;
    text-align: center;
    color: var(--color-gray-500);
    font-size: 0.8125rem;
  }

  @media (max-width: 640px) {
    .hero-section {
      padding: 3rem 0 4rem 0;
    }
    .features-section,
    .cta-section {
      padding: 3rem 0;
    }
    .features-grid {
      grid-template-columns: 1fr;
    }
  }
</style>
