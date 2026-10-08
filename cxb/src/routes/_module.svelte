<script lang="ts">
    import type { Snippet } from "svelte";
    import HomeHeader from "@/components/HomeHeader.svelte";
    import { ensureDmartAxios } from "@/lib/dmart_axios";

    // Root layout: the one place that runs for EVERY route, including the
    // password-reset pages outside /management. Without this, a direct load or
    // refresh there leaves Dmart.axiosDmartInstance undefined and every
    // request fails as a swallowed TypeError. main.ts awaits configReady
    // before mounting, so website.backend is already populated here.
    ensureDmartAxios();

    let { children }: { children?: Snippet } = $props();
</script>

<HomeHeader />
{@render children?.()}
