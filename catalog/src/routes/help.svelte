<script lang="ts">
  import {
    BookOpenSolid,
    ChevronDownOutline,
    EnvelopeSolid,
    MessagesSolid,
    QuestionCircleSolid,
  } from "flowbite-svelte-icons";
  import { _ } from "@/i18n";
  import { withBase } from "@/lib/paths";
  import { setTitle } from "@/lib/title";

  let openFaq: number | null = $state(null);
  const uid = $props.id();

  // $derived so a locale switch re-renders the questions (review #28).
  const faqs = $derived([
    { question: $_("help.faq.create_catalog.question"), answer: $_("help.faq.create_catalog.answer") },
    { question: $_("help.faq.collaborate.question"), answer: $_("help.faq.collaborate.answer") },
    { question: $_("help.faq.make_public.question"), answer: $_("help.faq.make_public.answer") },
    { question: $_("help.faq.content_types.question"), answer: $_("help.faq.content_types.answer") },
    { question: $_("help.faq.search.question"), answer: $_("help.faq.search.answer") },
    { question: $_("help.faq.export.question"), answer: $_("help.faq.export.answer") },
  ]);

  const quickHelp = $derived([
    {
      icon: BookOpenSolid,
      title: $_("help.quick_help.getting_started.title"),
      description: $_("help.quick_help.getting_started.description"),
      button: $_("help.quick_help.getting_started.button"),
      href: "/",
    },
    {
      icon: MessagesSolid,
      title: $_("help.quick_help.community_support.title"),
      description: $_("help.quick_help.community_support.description"),
      button: $_("help.quick_help.community_support.button"),
      href: "/community",
    },
    {
      icon: EnvelopeSolid,
      title: $_("help.quick_help.contact_support.title"),
      description: $_("help.quick_help.contact_support.description"),
      button: $_("help.quick_help.contact_support.button"),
      href: "/contact",
    },
  ]);

  function toggleFaq(index: number) {
    openFaq = openFaq === index ? null : index;
  }

  $effect(() => setTitle($_("HelpCenter")));
</script>

<div class="help-container">
  <section class="hero-section">
    <div class="hero-content">
      <div class="hero-text">
        <h1 class="hero-title">
          <span class="gradient-text">{$_("help.hero.title")}</span>
          {$_("help.hero.center")}
        </h1>
        <p class="hero-description">
          {$_("help.hero.description")}
        </p>
      </div>
    </div>
  </section>

  <section class="quick-help-section">
    <div class="quick-help-content">
      <h2 class="section-title">{$_("help.quick_help.title")}</h2>
      <div class="help-cards">
        {#each quickHelp as card (card.href)}
          <div class="help-card">
            <div class="help-icon" aria-hidden="true">
              <card.icon class="icon" color="currentColor" />
            </div>
            <h3>{card.title}</h3>
            <p>{card.description}</p>
            <a class="help-button" href={withBase(card.href)}>{card.button}</a>
          </div>
        {/each}
      </div>
    </div>
  </section>

  <section class="faq-section">
    <div class="faq-content">
      <h2 class="section-title">{$_("help.faq.title")}</h2>
      <div class="faq-list">
        {#each faqs as faq, index (index)}
          <div class="faq-item">
            <h3 class="faq-heading">
              <button
                type="button"
                class="faq-question"
                class:active={openFaq === index}
                aria-expanded={openFaq === index}
                aria-controls="{uid}-faq-{index}"
                onclick={() => toggleFaq(index)}
              >
                <span>{faq.question}</span>
                <ChevronDownOutline class="faq-chevron" aria-hidden="true" />
              </button>
            </h3>
            {#if openFaq === index}
              <div class="faq-answer" id="{uid}-faq-{index}">
                <p>{faq.answer}</p>
              </div>
            {/if}
          </div>
        {/each}
      </div>
    </div>
  </section>

  <section class="support-section">
    <div class="support-content">
      <div class="support-card">
        <QuestionCircleSolid class="icon" aria-hidden="true" />
        <h3>{$_("help.support.title")}</h3>
        <p>
          {$_("help.support.description")}
        </p>
        <a class="btn-support" href={withBase("/contact")}>{$_("help.support.button")}</a>
      </div>
    </div>
  </section>
</div>

<style>
  .help-container {
    min-height: 100vh;
    background: var(--color-surface);
  }

  .hero-section {
    padding: clamp(3rem, 6vw, 5rem) 0 clamp(2rem, 4vw, 3rem) 0;
    background: var(--gradient-page);
  }

  .hero-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

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

  .hero-description {
    font-size: 1.0625rem;
    color: var(--color-text-muted);
    max-width: 40rem;
    margin: 0 auto;
    line-height: 1.6;
  }

  .quick-help-section {
    padding: var(--space-section-y) 0;
    background: var(--color-surface-2);
  }

  .quick-help-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .section-title {
    font-size: 1.75rem;
    font-weight: 800;
    color: var(--color-text);
    text-align: center;
    margin-bottom: 2.5rem;
    letter-spacing: -0.02em;
  }

  .help-cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
    gap: 1.5rem;
  }

  .help-card {
    background: var(--color-surface-2);
    padding: 1.75rem;
    border-radius: var(--radius-card);
    box-shadow: var(--shadow-card);
    border: 1px solid var(--color-border);
    text-align: center;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .help-card:hover {
    transform: translateY(-4px);
    box-shadow: var(--shadow-lg);
    border-color: var(--color-primary-300);
  }

  .help-icon {
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

  .help-card h3 {
    font-size: 1.0625rem;
    font-weight: 700;
    color: var(--color-text);
    margin-bottom: 0.5rem;
  }

  .help-card p {
    color: var(--color-text-muted);
    line-height: 1.6;
    margin-bottom: 1.25rem;
    font-size: 0.9375rem;
  }

  .help-button {
    display: inline-flex;
    background: var(--gradient-brand);
    color: var(--color-text-on-primary);
    font-weight: 600;
    padding: 0.5rem 1.25rem;
    border-radius: var(--radius-control);
    border: none;
    cursor: pointer;
    text-decoration: none;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.8125rem;
    box-shadow: var(--shadow-brand);
  }

  .help-button:hover {
    background: var(--gradient-brand-hover);
    transform: translateY(-1px);
    box-shadow: var(--shadow-brand-lg);
  }

  .faq-section {
    padding: var(--space-section-y) 0;
    background: var(--color-surface);
  }

  .faq-content {
    max-width: 720px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .faq-list {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
  }

  .faq-item {
    background: var(--color-surface-2);
    border-radius: var(--radius-card);
    border: 1px solid var(--color-border);
    overflow: hidden;
  }

  .faq-heading {
    margin: 0;
    font-size: inherit;
    letter-spacing: normal;
  }

  .faq-question {
    width: 100%;
    padding: 1.125rem 1.25rem;
    background: none;
    border: none;
    text-align: start;
    cursor: pointer;
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.75rem;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-text);
    transition: all var(--duration-fast) ease;
  }

  .faq-question:hover { background: var(--color-surface-3); }

  .faq-question.active {
    background: var(--color-primary-soft);
    color: var(--color-primary);
  }

  .faq-question :global(.faq-chevron) {
    width: 1.125rem;
    height: 1.125rem;
    flex-shrink: 0;
    transition: transform var(--duration-normal) var(--ease-out);
  }

  .faq-question.active :global(.faq-chevron) {
    transform: rotate(180deg);
  }

  .faq-answer {
    padding: 0 1.25rem 1.25rem 1.25rem;
    border-top: 1px solid var(--color-border);
    background: var(--color-surface);
    animation: fadeInDown var(--duration-fast) var(--ease-out);
  }

  .faq-answer p {
    color: var(--color-text-muted);
    line-height: 1.6;
    margin: 0.875rem 0 0 0;
    font-size: 0.9375rem;
  }

  .support-section {
    padding: var(--space-section-y) 0;
    background: var(--color-surface-2);
  }

  .support-content {
    max-width: 520px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .support-card {
    background: var(--gradient-brand);
    padding: 2.5rem 2rem;
    border-radius: var(--radius-modal);
    text-align: center;
    color: var(--color-text-on-primary);
    position: relative;
    overflow: hidden;
  }

  .support-card::before {
    content: "";
    position: absolute;
    top: -40%;
    inset-inline-end: -25%;
    width: 50%;
    height: 80%;
    background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, transparent 60%);
    pointer-events: none;
  }

  .support-card :global(.icon) {
    width: 2.5rem;
    height: 2.5rem;
    margin: 0 auto 0.75rem;
  }

  .support-card h3 {
    font-size: 1.375rem;
    font-weight: 700;
    margin-bottom: 0.75rem;
    position: relative;
    color: inherit;
  }

  .support-card p {
    font-size: 0.9375rem;
    margin-bottom: 1.5rem;
    opacity: 0.85;
    line-height: 1.6;
    position: relative;
  }

  .btn-support {
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

  .btn-support:hover {
    background: var(--color-surface-3);
    transform: translateY(-1px);
    box-shadow: 0 4px 12px rgba(0,0,0,0.1);
  }

  @media (max-width: 640px) {
    .hero-section { padding: 2.5rem 0 1.5rem 0; }
    .quick-help-section, .faq-section, .support-section { padding: 2.5rem 0; }
    .help-cards { grid-template-columns: 1fr; }
    .support-card { padding: 2rem 1.5rem; }
  }
</style>
