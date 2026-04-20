<script lang="ts">
  export let src = "";
  export let alt = "Avatar";
  export let size: string | number = "200";

  let imgFailed = false;
  let hasValidSrc = false;

  $: hasValidSrc = !!src && src.trim() !== "";
  $: showImage = hasValidSrc && !imgFailed;
  $: sizePx = typeof size === "number" ? size : parseInt(String(size), 10) || 200;

  function getInitials(name : String) {
    if (!name || name === "Avatar") return "?";
    const parts = name.trim().split(/[\s._-]+/).filter(Boolean);
    if (parts.length >= 2) {
      return (parts[0].charAt(0) + parts[1].charAt(0)).toUpperCase();
    }
    return name.substring(0, 2).toUpperCase();
  }

  function handleError() {
    imgFailed = true;
  }

  const colors = [
    "#6366f1", "#8b5cf6", "#ec4899", "#f43f5e",
    "#f97316", "#eab308", "#22c55e", "#14b8a6",
    "#06b6d4", "#3b82f6",
  ];

  function getColor(name : String) {
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
    class="avatar"
    {src}
    {alt}
    height={sizePx}
    width={sizePx}
    on:error={handleError}
  />
{:else}
  <div
    class="avatar-initials"
    style="--avatar-size: {sizePx}px; --avatar-bg: {getColor(alt)};"
    title={alt}
  >
    {getInitials(alt)}
  </div>
{/if}

<style>
  .avatar {
    border-radius: 50%;
    object-fit: cover;
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
    color: #1f2937;
    font-weight: var(--font-weight-semibold);
    letter-spacing: 0.02em;
    user-select: none;
    flex-shrink: 0;
  }
</style>
