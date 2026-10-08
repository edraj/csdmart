<script module lang="ts">
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
    import { _ } from "@/i18n";
    import { withBase } from "@/lib/paths";

    let { url }: { url: string } = $props();
</script>

<div class="four04">
    <h1>{$_("not_found.title")}</h1>
    <p>
        {$_("not_found.message", { values: { url } })}
    </p>
    <!-- Root-absolute "/" would leave the <base href="/cat/"> deployment. -->
    <a href={withBase("/")}>{$_("not_found.go_home")}</a>
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
