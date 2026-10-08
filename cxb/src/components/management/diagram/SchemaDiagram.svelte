<script lang="ts">
    import { plantUmlSvgUrl, schemaVisualizationEncoder } from "@/utils/plantUML";
    import { website } from "@/config";
    import { _ } from "@/i18n";

    let {
        shortname,
        properties,
    }: {
        shortname: string;
        properties: Record<string, unknown> | undefined;
    } = $props();

    // The schema body is sent to the PlantUML server named in config.json
    // (`website.plantuml_server`); the public server is only the default.
    const svgUrl = $derived(plantUmlSvgUrl(website.plantuml_server, schemaVisualizationEncoder(properties)));
</script>

<div class="rounded-card border border-border bg-surface-2 shadow-card p-4 overflow-auto" dir="ltr">
    <a href={svgUrl} download="{shortname}.svg" class="inline-block rounded-control" title={$_("download_diagram")}>
        <img src={svgUrl} alt={$_("schema_diagram_alt", { values: { shortname } })} class="max-w-full h-auto" loading="lazy" decoding="async" />
    </a>
</div>
