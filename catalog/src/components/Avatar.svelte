<script lang="ts">
  // A user's picture, or their initials on a stable per-name colour when there
  // is none (or it fails to load). `alt` is the person's name; leave it empty
  // when the name is printed right next to the avatar so screen readers do
  // not hear it twice.
  let {
    src = "",
    alt = "",
    size = 200,
    class: className = "",
  }: { src?: string | null; alt?: string; size?: string | number; class?: string } = $props();

  let imgFailed = $state(false);

  const hasValidSrc = $derived(!!src && src.trim() !== "");
  const showImage = $derived(hasValidSrc && !imgFailed);
  const sizePx = $derived(
    typeof size === "number" ? size : parseInt(String(size), 10) || 200,
  );

  function getInitials(name: string) {
    if (!name) return "?";
    const parts = name.trim().split(/[\s._-]+/).filter(Boolean);
    if (parts.length >= 2) {
      return (parts[0].charAt(0) + parts[1].charAt(0)).toUpperCase();
    }
    return name.substring(0, 2).toUpperCase();
  }

  const colors = [
    "#4f46e5", "#7c3aed", "#db2777", "#e11d48",
    "#ea580c", "#ca8a04", "#16a34a", "#0d9488",
    "#0891b2", "#2563eb",
  ];

  function getColor(name: string) {
    if (!name) return colors[0];
    let hash = 0;
    for (let i = 0; i < name.length; i++) {
      hash = name.charCodeAt(i) + ((hash << 5) - hash);
    }
    return colors[Math.abs(hash) % colors.length];
  }
</script>

{#if showImage}
  <img
    class="avatar {className}"
    {src}
    {alt}
    height={sizePx}
    width={sizePx}
    loading="lazy"
    decoding="async"
    onerror={() => (imgFailed = true)}
  />
{:else}
  <div
    class="avatar-initials {className}"
    style="--avatar-size: {sizePx}px; --avatar-bg: {getColor(alt)};"
    role={alt ? "img" : undefined}
    aria-label={alt || undefined}
    aria-hidden={alt ? undefined : "true"}
  >
    {getInitials(alt)}
  </div>
{/if}

<style>
  .avatar {
    border-radius: 50%;
    object-fit: cover;
    flex-shrink: 0;
  }

  .avatar-initials {
    width: var(--avatar-size);
    height: var(--avatar-size);
    font-size: max(calc(var(--avatar-size) * 0.38), 10px);
    background: var(--avatar-bg);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--color-text-on-primary);
    font-weight: var(--font-weight-semibold);
    letter-spacing: 0.02em;
    user-select: none;
    flex-shrink: 0;
  }
</style>
