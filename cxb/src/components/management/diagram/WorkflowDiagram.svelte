<script lang="ts">
    import { encode } from "plantuml-encoder";
    import { jsonToPlantUML } from "@/utils/renderer/workflowRendererUtils";
    import { plantUmlSvgUrl } from "@/utils/plantUML";
    import { website } from "@/config";
    import { _ } from "@/i18n";

    let {
        shortname,
        workflowContent,
    }: {
        shortname: string;
        workflowContent: any;
    } = $props();

    // The workflow body is sent to the PlantUML server named in config.json
    // (`website.plantuml_server`); the public server is only the default.
    const svgUrl = $derived(plantUmlSvgUrl(website.plantuml_server, encode(jsonToPlantUML(workflowContent))));
</script>

<div class="rounded-card border border-border bg-surface-2 shadow-card p-4 overflow-auto text-center" dir="ltr">
    <a href={svgUrl} download="{shortname}.svg" class="inline-block rounded-control" title={$_("download_diagram")}>
        <img src={svgUrl} alt={$_("workflow_diagram_alt", { values: { shortname } })} class="max-w-full h-auto mx-auto" loading="lazy" decoding="async" />
    </a>
</div>
