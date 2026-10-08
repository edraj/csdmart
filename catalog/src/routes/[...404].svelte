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
    import { QuestionCircleOutline } from "flowbite-svelte-icons";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import { _ } from "@/i18n";
    import { withBase } from "@/lib/paths";
    import { setTitle } from "@/lib/title";

    let { url }: { url: string } = $props();

    $effect(() => setTitle($_("not_found.title")));
</script>

<div class="four04">
    <EmptyState
        icon={QuestionCircleOutline}
        title={$_("not_found.title")}
        hint={$_("not_found.message", { values: { url } })}
    >
        <!-- Root-absolute "/" would leave the <base href="/cat/"> deployment. -->
        <a class="app-btn app-btn-primary" href={withBase("/")}>{$_("not_found.go_home")}</a>
    </EmptyState>
</div>

<style>
    .four04 {
        max-width: 36rem;
        margin: 0 auto;
        padding: clamp(2rem, 8vw, 5rem) var(--space-page-x);
    }
</style>
