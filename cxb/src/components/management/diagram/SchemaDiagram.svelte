<script lang="ts">
    import {plantUmlSvgUrl, schemaVisualizationEncoder} from "@/utils/plantUML";
    import {website} from "@/config";

    let { shortname, properties }: {
        shortname: string;
        properties: Record<string, unknown> | undefined;
    } = $props();

    // The schema body is sent to the PlantUML server named in config.json
    // (`website.plantuml_server`); the public server is only the default.
    const svgUrl = $derived(
        plantUmlSvgUrl(website.plantuml_server, schemaVisualizationEncoder(properties)),
    );
</script>

<a href={svgUrl} download="{shortname}.svg">
    <img src={svgUrl} alt={shortname} />
</a>
