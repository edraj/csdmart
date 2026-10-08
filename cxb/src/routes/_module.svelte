<script lang="ts">
    import HomeHeader from "@/components/HomeHeader.svelte";
    import { ensureDmartAxios } from "@/lib/dmart_axios";

    // Root layout: the one place that runs for EVERY route, including the
    // password-reset pages outside /management. Without this, a direct load or
    // refresh there leaves Dmart.axiosDmartInstance undefined and every
    // request fails as a swallowed TypeError. main.ts awaits configReady
    // before mounting, so website.backend is already populated here.
    ensureDmartAxios();

</script>

<HomeHeader />
<!-- Routify 3.6 renders route components with `let:` directives
     (RenderFragment.svelte), so the child route arrives as a Svelte 4 slot;
     a layout that renders {@render children()} throws
     invalid_default_snippet at runtime. svelte-check flags the slot as
     deprecated; ESLint's compile pass does not, hence both comments. -->
<!-- eslint-disable-next-line svelte/no-unused-svelte-ignore -- only svelte-check emits slot_element_deprecated here -->
<!-- svelte-ignore slot_element_deprecated -->
<slot />
