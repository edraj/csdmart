import { Dmart, QueryType, ResourceType, DmartScope } from "@edraj/tsdmart";
import { getEntityByShortname } from "./core";
import { log } from "@/lib/logger";

export async function fetchWorkflows(space_name: string) {
    try {
        const result = await Dmart.query({
            search: "",
            type: QueryType.search,
            space_name,
            subpath: "/workflows",
        });
        return result?.records || [];
    } catch (e) {
        log.error("Failed to fetch workflows:", e);
        return [];
    }
}

export async function getWorkflow(shortname: string, space_name: string = "catalog") {
    try {
        return await getEntityByShortname(
            shortname,
            space_name,
            "workflows",
            ResourceType.content,
            DmartScope.managed,
            true,
            true
        );
    } catch (e) {
        log.error(`Failed to fetch workflow ${shortname}`);
        return null;
    }
}
