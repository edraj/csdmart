<script context="module" lang="ts">
    // User-level override of .routify/components/[...404].svelte. Exists so
    // svelte-check sees a typed `route` parameter instead of implicitly `any`
    // in the auto-generated file. Routify picks up the root-level catch-all
    // and skips the default when one is present (see comment at the top of
    // the generated file for details).
    export const load = ({ route }: { route: { url: string } }) => ({
        status: 404,
        error: "[Routify] Page could not be found.",
        props: { url: route.url },
    });
</script>

<script lang="ts">
    export let url: string;
</script>

<div class="four04">
    <h1>404 - Page Not Found</h1>
    <p>
        The page <code>{url}</code> could not be found. Please check the URL or go back
        to the <a href="/">homepage</a>.
    </p>
</div>

<style>
    div.four04 {
        display: flex;
        align-items: center;
        flex-direction: column;
        text-align: center;
    }
    div.four04 > * {
        margin-top: 1em;
    }
</style>
