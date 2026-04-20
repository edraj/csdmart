<script lang="ts">
  interface Props {
    width?: string;
    height?: string;
    radius?: string;
    class?: string;
  }

  let {
    width = "100%",
    height = "1rem",
    radius = "var(--radius-md)",
    class: className = "",
  }: Props = $props();
</script>

<div
  class="skeleton-block {className}"
  style="--sk-width: {width}; --sk-height: {height}; --sk-radius: {radius};"
  aria-hidden="true"
></div>

<style>
  .skeleton-block {
    width: var(--sk-width);
    height: var(--sk-height);
    border-radius: var(--sk-radius);
    background: linear-gradient(
      90deg,
      var(--color-gray-100) 0%,
      var(--color-gray-200) 50%,
      var(--color-gray-100) 100%
    );
    background-size: 200% 100%;
    animation: shimmer 1.4s linear infinite;
    display: block;
  }

  @media (prefers-reduced-motion: reduce) {
    .skeleton-block {
      animation: none;
      background: var(--color-gray-100);
    }
  }
</style>
