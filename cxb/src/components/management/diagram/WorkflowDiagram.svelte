<script lang="ts">
    import {encode} from "plantuml-encoder";
    import {jsonToPlantUML} from "@/utils/renderer/workflowRendererUtils";
    import {plantUmlSvgUrl} from "@/utils/plantUML";
    import {website} from "@/config";

    let { shortname, workflowContent } : {
        shortname: string,
        workflowContent: any
    } = $props();

    // The workflow body is sent to the PlantUML server named in config.json
    // (`website.plantuml_server`); the public server is only the default.
    const svgUrl = $derived(
        plantUmlSvgUrl(website.plantuml_server, encode(jsonToPlantUML(workflowContent))),
    );
</script>


<div
  class="px-1 pb-1 h-full w-full"
  style="text-align: left; direction: ltr; overflow: hidden auto;"
>
  <div class="preview">
    <a href={svgUrl} download="{shortname}.svg">
      <img src={svgUrl} alt={shortname} />
    </a>
  </div>
</div>


<style>
  .preview {
    width: 100%;
    text-align: center;
  }
</style>
