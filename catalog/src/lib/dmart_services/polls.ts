import {
    type ApiQueryResponse,
    ContentType,
    ResourceType,
    DmartScope,
    SortType,
} from "@edraj/tsdmart";
import {
    createEntity,
    updateEntity,
    searchEntities
} from "./core";
import { APPLICATIONS_SPACE } from "@/lib/constants";

export async function getPolls(
    space_name: string = "applications",
    scope: DmartScope = DmartScope.managed,
    limit = 100,
    offset = 0,
    exact_subpath = false
): Promise<ApiQueryResponse> {
    return await searchEntities(
        space_name,
        "/polls",
        "-@shortname:schema",
        limit,
        offset,
        "shortname",
        SortType.ascending,
        scope,
        true,
        true,
        exact_subpath
    );
}

export async function userVote(
    poll_shortname: string,
    candidate_shortname: string,
    voters: any,
    isReplace: boolean = false
) {
    const attributes = {
        is_active: true,
        payload: {
            content_type: ContentType.json,
            body: { voters },
        },
    };

    if (isReplace) {
        return await updateEntity(
            candidate_shortname,
            APPLICATIONS_SPACE,
            `polls/${poll_shortname}`,
            ResourceType.json,
            attributes
        );
    } else {
        return await createEntity(
            APPLICATIONS_SPACE,
            `polls/${poll_shortname}`,
            ResourceType.json,
            attributes,
            candidate_shortname
        );
    }
}
