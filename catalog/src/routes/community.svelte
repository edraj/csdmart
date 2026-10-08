<script lang="ts">
  import { GlobeSolid, HeartSolid, MessageCaptionSolid, UsersSolid } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  // $derived so a locale switch re-renders the labels (review #28).
  const communityFeatures = $derived([
    {
      icon: UsersSolid,
      title: $_("community.features.connect.title"),
      description: $_("community.features.connect.description"),
    },
    {
      icon: MessageCaptionSolid,
      title: $_("community.features.discussions.title"),
      description: $_("community.features.discussions.description"),
    },
    {
      icon: HeartSolid,
      title: $_("community.features.support.title"),
      description: $_("community.features.support.description"),
    },
    {
      icon: GlobeSolid,
      title: $_("community.features.global.title"),
      description: $_("community.features.global.description"),
    },
  ]);

  const guidelines = $derived([
    { title: $_("community.guidelines.respectful.title"), description: $_("community.guidelines.respectful.description") },
    { title: $_("community.guidelines.quality.title"), description: $_("community.guidelines.quality.description") },
    { title: $_("community.guidelines.collaborate.title"), description: $_("community.guidelines.collaborate.description") },
    { title: $_("community.guidelines.on_topic.title"), description: $_("community.guidelines.on_topic.description") },
  ]);

  $effect(() => setTitle($_("Community")));
</script>

<div class="community-container">
  <section class="hero-section">
    <div class="hero-content">
      <div class="hero-text">
        <h1 class="hero-title">
          {$_("community.hero.join")}
          <span class="gradient-text">{$_("community.hero.community")}</span>
        </h1>
        <p class="hero-description">
          {$_("community.hero.description")}
        </p>
        <div class="hero-buttons">
          <a class="btn-primary" href={withBase("/register")}>{$_("community.hero.join_button")}</a>
          <a class="btn-secondary" href={withBase("/")}>{$_("community.hero.explore_button")}</a>
        </div>
      </div>
    </div>
  </section>

  <section class="features-section">
    <div class="features-content">
      <div class="features-header">
        <h2 class="features-title">{$_("community.why_join.title")}</h2>
        <p class="features-description">
          {$_("community.why_join.description")}
        </p>
      </div>

      <div class="features-grid">
        {#each communityFeatures as feature (feature.title)}
          <div class="feature-card">
            <div class="feature-icon" aria-hidden="true">
              <feature.icon class="icon" color="currentColor" />
            </div>
            <h3 class="feature-title">{feature.title}</h3>
            <p class="feature-description">{feature.description}</p>
          </div>
        {/each}
      </div>
    </div>
  </section>

  <section class="guidelines-section">
    <div class="guidelines-content">
      <h2 class="guidelines-title">{$_("community.guidelines.title")}</h2>
      <div class="guidelines-grid">
        {#each guidelines as item (item.title)}
          <div class="guideline-item">
            <h3>{item.title}</h3>
            <p>{item.description}</p>
          </div>
        {/each}
      </div>
    </div>
  </section>
</div>

<style>
  .community-container {
    min-height: 100vh;
    background: var(--color-surface);
  }

  .hero-section {
    padding: clamp(4rem, 8vw, 6rem) 0;
    background: var(--gradient-page);
    position: relative;
    overflow: hidden;
  }

  .hero-section::before {
    content: "";
    position: absolute;
    top: -20%;
    inset-inline-end: -10%;
    width: 50%;
    height: 70%;
    background: radial-gradient(circle, rgba(99, 102, 241, 0.07) 0%, transparent 65%);
    pointer-events: none;
  }

  .hero-content { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); position: relative; z-index: 1; }
  .hero-text { text-align: center; }

  .hero-title {
    font-size: clamp(2.25rem, 5vw, 3.5rem);
    font-weight: 800;
    color: var(--color-text);
    margin-bottom: 1.25rem;
    line-height: 1.1;
    letter-spacing: -0.03em;
  }

  .gradient-text {
    background: var(--gradient-brand);
    -webkit-background-clip: text;
    background-clip: text;
    -webkit-text-fill-color: transparent;
  }

  .hero-description {
    font-size: 1.0625rem;
    color: var(--color-text-muted);
    margin-bottom: 2.5rem;
    max-width: 42rem;
    margin-inline: auto;
    line-height: 1.65;
  }

  .hero-buttons { display: flex; flex-direction: column; gap: 0.75rem; justify-content: center; align-items: center; }
  @media (min-width: 640px) { .hero-buttons { flex-direction: row; } }

  .btn-primary,
  .btn-secondary {
    display: inline-flex;
    align-items: center;
    justify-content: center;
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

  .btn-primary:hover { background: var(--gradient-brand-hover); transform: translateY(-2px); box-shadow: var(--shadow-brand-lg); }
  .btn-primary:active { transform: translateY(0); }

  .btn-secondary {
    background: var(--color-surface-2);
    color: var(--color-text);
    border: 1.5px solid var(--color-border);
  }

  .btn-secondary:hover { background: var(--color-surface-3); border-color: var(--color-border-strong); transform: translateY(-1px); box-shadow: var(--shadow-sm); }

  .features-section { padding: var(--space-section-y) 0; background: var(--color-surface-2); }
  .features-content { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); }
  .features-header { text-align: center; margin-bottom: 2.5rem; }

  .features-title { font-size: clamp(1.75rem, 3.5vw, 2.25rem); font-weight: 800; color: var(--color-text); margin-bottom: 0.75rem; letter-spacing: -0.02em; }
  .features-description { font-size: 1.0625rem; color: var(--color-text-muted); max-width: 30rem; margin: 0 auto; line-height: 1.6; }

  .features-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 1.5rem; }

  .feature-card {
    background: var(--color-surface-2);
    padding: 1.75rem;
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    transition: all var(--duration-normal) var(--ease-out);
  }

  .feature-card:hover { transform: translateY(-4px); box-shadow: var(--shadow-lg); border-color: var(--color-primary-300); }

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

  .feature-title { font-size: 1.0625rem; font-weight: 700; color: var(--color-text); margin-bottom: 0.5rem; }
  .feature-description { color: var(--color-text-muted); line-height: 1.6; font-size: 0.9375rem; }

  .guidelines-section { padding: var(--space-section-y) 0; background: var(--color-surface); }
  .guidelines-content { max-width: 1100px; margin: 0 auto; padding: 0 var(--space-page-x); }

  .guidelines-title { font-size: clamp(1.75rem, 3.5vw, 2.25rem); font-weight: 800; color: var(--color-text); text-align: center; margin-bottom: 2.5rem; letter-spacing: -0.02em; }

  .guidelines-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 1.5rem; }

  .guideline-item {
    background: var(--color-surface-2);
    padding: 1.75rem;
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    transition: all var(--duration-normal) var(--ease-out);
  }

  .guideline-item:hover { box-shadow: var(--shadow-md); }
  .guideline-item h3 { font-size: 1.0625rem; font-weight: 700; color: var(--color-text); margin-bottom: 0.5rem; }
  .guideline-item p { color: var(--color-text-muted); line-height: 1.6; font-size: 0.9375rem; }

  @media (max-width: 640px) {
    .hero-section { padding: 3rem 0; }
    .features-section, .guidelines-section { padding: 2.5rem 0; }
  }
</style>
