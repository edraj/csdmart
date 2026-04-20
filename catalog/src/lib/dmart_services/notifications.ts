import {
    type QueryRequest,
    Dmart,
    QueryType,
    RequestType,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { PERSONAL_SPACE } from "@/lib/constants";

export async function fetchMyNotifications(shortname: string) {
    const data: QueryRequest = {
        filter_shortnames: [],
        type: QueryType.search,
        space_name: PERSONAL_SPACE,
        subpath: `people/${shortname}/notifications`,
        limit: 100,
        sort_by: "shortname",
        sort_type: SortType.descending,
        offset: 0,
        search: "",
        retrieve_json_payload: true,
    };
    const response = await Dmart.query(data);
    return response?.records ?? [];
}

export async function markNotification(
    user_shortname: string,
    shortname: string,
    markRead: boolean = true
) {
    const response = await Dmart.request({
        space_name: PERSONAL_SPACE,
        request_type: RequestType.update,
        records: [
            {
                resource_type: ResourceType.content,
                shortname: shortname,
                subpath: `people/${user_shortname}/notifications`,
                attributes: {
                    payload: {
                        schema_shortname: null,
                        body: {
                            is_read: markRead ? "yes" : "no",
                        },
                    },
                },
            },
        ],
    });
    return response.status === "success";
}

export async function deleteAllNotification(
    user_shortname: string,
    shortnames: string[]
) {
    const response = await Dmart.request({
        space_name: PERSONAL_SPACE,
        request_type: RequestType.delete,
        records: shortnames.map((shortname) => ({
            resource_type: ResourceType.content,
            shortname: shortname,
            subpath: `people/${user_shortname}/notifications`,
            attributes: {},
        })),
    });
    return response.status === "success";
}
