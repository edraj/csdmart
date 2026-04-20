<script lang="ts">
    import {goto} from "@roxi/routify";
    import {
        BookOpenSolid,
        ChevronDownOutline,
        EnvelopeSolid,
        MessagesSolid,
        QuestionCircleSolid,
    } from "flowbite-svelte-icons";
    import {_} from "@/i18n";

    let openFaq: any = $state(null);
  $goto;
  const faqs = [
    {
      question: $_("help.faq.create_catalog.question"),
      answer: $_("help.faq.create_catalog.answer"),
    },
    {
      question: $_("help.faq.collaborate.question"),
      answer: $_("help.faq.collaborate.answer"),
    },
    {
      question: $_("help.faq.make_public.question"),
      answer: $_("help.faq.make_public.answer"),
    },
    {
      question: $_("help.faq.content_types.question"),
      answer: $_("help.faq.content_types.answer"),
    },
    {
      question: $_("help.faq.search.question"),
      answer: $_("help.faq.search.answer"),
    },
    {
      question: $_("help.faq.export.question"),
      answer: $_("help.faq.export.answer"),
    },
  ];

  function toggleFaq(index: any) {
    openFaq = openFaq === index ? null : index;
  }

  function handleContactSupport() {
    $goto("/contact");
  }

  function handleJoinCommunity() {
    $goto("/community");
  }

  function handleExploreCatalogs() {
    $goto("/");
  }
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
        <div class="help-card">
          <div class="help-icon">
            <BookOpenSolid class="icon" color="white" />
          </div>
          <h3>{$_("help.quick_help.getting_started.title")}</h3>
          <p>{$_("help.quick_help.getting_started.description")}</p>
          <button class="help-button" onclick={handleExploreCatalogs}>
            {$_("help.quick_help.getting_started.button")}
          </button>
        </div>
        <div class="help-card">
          <div class="help-icon">
            <MessagesSolid class="icon" color="white" />
          </div>
          <h3>{$_("help.quick_help.community_support.title")}</h3>
          <p>{$_("help.quick_help.community_support.description")}</p>
          <button class="help-button" onclick={handleJoinCommunity}>
            {$_("help.quick_help.community_support.button")}
          </button>
        </div>
        <div class="help-card">
          <div class="help-icon">
            <EnvelopeSolid class="icon" color="white" />
          </div>
          <h3>{$_("help.quick_help.contact_support.title")}</h3>
          <p>{$_("help.quick_help.contact_support.description")}</p>
          <button class="help-button" onclick={handleContactSupport}>
            {$_("help.quick_help.contact_support.button")}
          </button>
        </div>
      </div>
    </div>
  </section>

  <section class="faq-section">
    <div class="faq-content">
      <h2 class="section-title">{$_("help.faq.title")}</h2>
      <div class="faq-list">
        {#each faqs as faq, index}
          <div class="faq-item">
            <button
              aria-label={$_("route_labels.aria_toggle_faq") + " " + (index + 1)}
              class="faq-question"
              onclick={() => toggleFaq(index)}
              class:active={openFaq === index}
            >
              <span>{faq.question}</span>
              <ChevronDownOutline class={openFaq === index ? "rotated" : ""} />
            </button>
            {#if openFaq === index}
              <div class="faq-answer">
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
        <QuestionCircleSolid class="icon" />
        <h3>{$_("help.support.title")}</h3>
        <p>
          {$_("help.support.description")}
        </p>
        <button class="btn-support" onclick={handleContactSupport}>
          {$_("help.support.button")}
        </button>
      </div>
    </div>
  </section>
</div>

<style>
  .help-container {
    min-height: 100vh;
    background: var(--surface-page);
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

  .hero-description {
    font-size: 1.0625rem;
    color: var(--color-gray-500);
    max-width: 40rem;
    margin: 0 auto;
    line-height: 1.6;
  }

  .quick-help-section {
    padding: var(--space-section-y) 0;
    background: white;
  }

  .quick-help-content {
    max-width: 1100px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .section-title {
    font-size: 1.75rem;
    font-weight: 800;
    color: var(--color-gray-900);
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
    background: white;
    padding: 1.75rem;
    border-radius: var(--radius-xl);
    box-shadow: var(--shadow-sm);
    border: 1px solid var(--color-gray-100);
    text-align: center;
    transition: all var(--duration-normal) var(--ease-out);
  }

  .help-card:hover {
    transform: translateY(-4px);
    box-shadow: var(--shadow-xl);
    border-color: var(--color-primary-100);
  }

  .help-icon {
    width: 2.75rem;
    height: 2.75rem;
    background: var(--gradient-brand);
    border-radius: var(--radius-lg);
    display: flex;
    align-items: center;
    justify-content: center;
    margin: 0 auto 1.25rem auto;
    box-shadow: var(--shadow-brand);
  }

  .help-card h3 {
    font-size: 1.0625rem;
    font-weight: 700;
    color: var(--color-gray-800);
    margin-bottom: 0.5rem;
  }

  .help-card p {
    color: var(--color-gray-500);
    line-height: 1.6;
    margin-bottom: 1.25rem;
    font-size: 0.9375rem;
  }

  .help-button {
    background: var(--gradient-brand);
    color: white;
    font-weight: 600;
    padding: 0.5rem 1.25rem;
    border-radius: var(--radius-lg);
    border: none;
    cursor: pointer;
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
    background: var(--color-gray-50);
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
    background: white;
    border-radius: var(--radius-lg);
    border: 1px solid var(--color-gray-200);
    overflow: hidden;
  }

  .faq-question {
    width: 100%;
    padding: 1.125rem 1.25rem;
    background: none;
    border: none;
    text-align: left;
    cursor: pointer;
    display: flex;
    justify-content: space-between;
    align-items: center;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-gray-800);
    transition: all var(--duration-fast) ease;
  }

  .faq-question:hover { background: var(--color-gray-50); }

  .faq-question.active {
    background: var(--color-primary-50);
    color: var(--color-primary-600);
  }

  .faq-answer {
    padding: 0 1.25rem 1.25rem 1.25rem;
    border-top: 1px solid var(--color-gray-100);
    background: var(--color-gray-50);
    animation: fadeInDown var(--duration-fast) var(--ease-out);
  }

  .faq-answer p {
    color: var(--color-gray-500);
    line-height: 1.6;
    margin: 0.875rem 0 0 0;
    font-size: 0.9375rem;
  }

  .support-section {
    padding: var(--space-section-y) 0;
    background: white;
  }

  .support-content {
    max-width: 520px;
    margin: 0 auto;
    padding: 0 var(--space-page-x);
  }

  .support-card {
    background: var(--gradient-brand);
    padding: 2.5rem 2rem;
    border-radius: var(--radius-2xl);
    text-align: center;
    color: white;
    position: relative;
    overflow: hidden;
  }

  .support-card::before {
    content: "";
    position: absolute;
    top: -40%;
    right: -25%;
    width: 50%;
    height: 80%;
    background: radial-gradient(circle, rgba(255,255,255,0.1) 0%, transparent 60%);
    pointer-events: none;
  }

  .support-card h3 {
    font-size: 1.375rem;
    font-weight: 700;
    margin-bottom: 0.75rem;
    position: relative;
  }

  .support-card p {
    font-size: 0.9375rem;
    margin-bottom: 1.5rem;
    opacity: 0.85;
    line-height: 1.6;
    position: relative;
  }

  .btn-support {
    background: white;
    color: var(--color-primary-600);
    font-weight: 600;
    padding: 0.625rem 1.5rem;
    border-radius: var(--radius-lg);
    border: none;
    cursor: pointer;
    transition: all var(--duration-normal) var(--ease-out);
    font-size: 0.9375rem;
    position: relative;
  }

  .btn-support:hover {
    background: var(--color-gray-50);
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
