<script module lang="ts">
    import { createRouter, Router } from "@roxi/routify";
    import routes from "../.routify/routes.default";
    import { SvelteToast, type SvelteToastOptions } from "@zerodevx/svelte-toast";
    import "./app.css";
    import { theme } from "@/stores/theme.svelte";

    // Initialise theme controller (side-effect: sets .dark class and color-scheme)
    void theme;

    // Derive router prefix from <base href> in index.html (strip leading/trailing slashes)
    const baseHref = document.querySelector("base")?.getAttribute("href") || "/";
    const prefix = baseHref.replace(/^\/|\/$/g, "");

    const options: SvelteToastOptions = {
        duration: 2500, // duration of progress bar tween to the `next` value
        initial: 1, // initial progress bar value
        next: 0, // next progress value
        pausable: false, // pause progress bar tween on mouse hover
        dismissable: true, // allow dismiss with close button
        reversed: false, // insert new toast to bottom of stack
        intro: { x: 256 }, // toast intro fly animation settings
        theme: {
            "--toastColor": "mintcream",
        }, // css var overrides
        classes: ["custom-toast"], // user-defined classes
    };
</script>

<script lang="ts">
    import { setupI18n } from "./i18n";

    // The URL rewriter that used to live here looked for `.en`/`.ar`/`.ku`
    // page variants that this app has never had; the only rewrite that matters
    // is stripping and re-adding the <base href> prefix.
    const router = createRouter({
        routes,
        urlRewrite: {
            toInternal: (url) => {
                if (url.startsWith(`/${prefix}`)) {
                    url = url.replace(`/${prefix}`, "");
                }
                return url === "" ? "/" : url;
            },
            toExternal: (url) => `/${prefix}${url}`,
        },
    });

    // Also keeps <html lang dir> in step with the locale.
    setupI18n();
</script>

<div id="routify-app">
    <SvelteToast {options} />
    <Router {router} />
</div>

<style lang="postcss">
    :global(.custom-toast.success),
    :global(.custom-toast.info) {
        --toastBackground: var(--color-success);
        --toastBarBackground: #0f7a36;
        z-index: 999;
    }
    :global(.custom-toast.error),
    :global(.custom-toast.warn) {
        --toastBackground: var(--color-danger);
        --toastBarBackground: #8e1a1a;
        --toastContainerZIndex: 99999999999999;
    }
    :global(.custom-toast.warning) {
        --toastBackground: var(--color-warning);
        --toastBarBackground: #a0560a;
        z-index: 999;
    }
    :global(.custom-toast.informative) {
        --toastBackground: var(--color-info);
        --toastBarBackground: #1d4fa4;
        z-index: 999;
    }
</style>
