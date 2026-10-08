import {encode} from "plantuml-encoder";

const startjsonForPlantUML = '@startjson\n<style>\njsonDiagram {  node {BackGroundColor business}  \nhighlight {BackGroundColor greenyellow}}\n</style>\n#highlight "*" / "*_shortname"\n';

/** Where diagrams are rendered when `website.plantuml_server` is not configured. */
export const DEFAULT_PLANTUML_SERVER = "https://www.plantuml.com/plantuml";

/**
 * URL of the rendered SVG for an already-encoded diagram.
 *
 * The schema and workflow bodies are sent to this server for rendering, so a
 * deployment that must keep them in-house points `website.plantuml_server` at
 * its own instance. A blank or whitespace server falls back to the public one;
 * trailing slashes are tolerated.
 */
export function plantUmlSvgUrl(server: string | null | undefined, encoded: string): string {
    const base = (server ?? "").trim().replace(/\/+$/, "") || DEFAULT_PLANTUML_SERVER;
    return `${base}/svg/${encoded}`;
}

function schemaVisualizationParser(properties) {
    const output: any = {};

    for (const key in properties) {
        const property = properties[key];

        if (property.type === "object" && property.properties) {
            output[key] = schemaVisualizationParser(property.properties);
        } else if (property.properties && property.properties.code) {
            output[key] = property.properties.code.type;
        } else {
            output[key] = property.type;

            if (property.type === "array") {
                output[key] += " of " + property?.items?.type || "unknown";
                if (property?.items?.enum) {
                    output[key] = { type: output[key], enum: property.items.enum };
                }
            }

            if (property.pattern) {
                output[key] += `/pattern\\n${property.pattern}`;
            }
        }
    }

    return output;
}

export function schemaVisualizationEncoder(entry) {
    try {
        const content = `${startjsonForPlantUML}\n${JSON.stringify(
            schemaVisualizationParser(entry),
            null,
            2
        )}\n@endjson`;
        const currentDiagram = {
            name: "",
            content,
            encodedContent: function () {
                return encode(this.content);
            },
        };
        return currentDiagram.encodedContent();
    } catch {
        return {
            name: "",
            content: `${startjsonForPlantUML}\n{"error": "something is wrong with the schema"}\n@endjson`,
            encodedContent: function () {
                return encode(this.content);
            },
        }.encodedContent();
    }
}
