import {
    type ApiQueryResponse,
    ContentType,
    DmartScope,
    ResourceType,
    SortType,
} from "@edraj/tsdmart";
import { user } from "@/stores/user";
import { get } from "svelte/store";
import { log } from "@/lib/logger";
import {
    getEntity,
    createEntity,
    updateEntity,
    searchEntities
} from "./core";
import { APPLICATIONS_SPACE } from "@/lib/constants";
import { attachmentGroup, type EntryRecord, type JsonObject } from "@/lib/types";

export async function getSurveys(
    space_name: string = "applications",
    scope: DmartScope = DmartScope.managed,
    limit = 100,
    offset = 0,
    exact_subpath = false
): Promise<ApiQueryResponse> {
    return await searchEntities(
        space_name,
        "/surveys",
        "@resource_type:content",
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

export async function submitSurveyResponse(
    survey_shortname: string,
    responses: JsonObject
) {
    const currentUser = get(user);
    if (!currentUser?.shortname) {
        throw new Error("User not authenticated");
    }

    const existingResponse = await getUserSurveyResponseRecord(survey_shortname);

    const attributes = {
        is_active: true,
        owner_shortname: currentUser.shortname,
        payload: {
            content_type: ContentType.json,
            body: responses,
        },
    };

    if (existingResponse) {
        return await updateEntity(
            existingResponse.shortname,
            APPLICATIONS_SPACE,
            `surveys/${survey_shortname}`,
            ResourceType.json,
            attributes
        );
    } else {
        return await createEntity(
            APPLICATIONS_SPACE,
            `surveys/${survey_shortname}`,
            ResourceType.json,
            attributes,
            "auto"
        );
    }
}

/**
 * The current user's response to a survey: the `json` attachment of the
 * survey entry that the user owns, or null when they have not answered.
 */
export async function getUserSurveyResponseRecord(
    survey_shortname: string
): Promise<EntryRecord | null> {
    const currentUser = get(user);
    if (!currentUser?.shortname) {
        return null;
    }

    try {
        const survey = await getEntity(
            survey_shortname,
            APPLICATIONS_SPACE,
            "surveys",
            ResourceType.content,
            DmartScope.managed,
            true,
            true
        );

        const responses = attachmentGroup(survey?.attachments, "json");
        if (responses.length === 0) {
            return null;
        }

        const userResponse = responses.find(
            (attachment) =>
                attachment.attributes?.owner_shortname === currentUser.shortname
        );

        return userResponse || null;
    } catch (error) {
        log.error("Error getting user survey response record:", error);
        return null;
    }
}

export async function hasUserRespondedToSurvey(
    survey_shortname: string
): Promise<boolean> {
    const response = await getUserSurveyResponseRecord(survey_shortname);
    return !!response;
}

/** The answers the current user submitted (the response's payload body), or null. */
export async function getUserSurveyResponses(
    survey_shortname: string
): Promise<unknown> {
    const userResponse = await getUserSurveyResponseRecord(survey_shortname);
    return userResponse?.attributes?.payload?.body || null;
}

export async function getAllSurveyResponses() {
    const response = await searchEntities(
        APPLICATIONS_SPACE,
        "/surveys",
        "@resource_type:json",
        1000,
        0,
        "created_at",
        SortType.descending
    );
    return response.records || [];
}

export async function getUserSurveys() {
    const currentUser = get(user);
    if (!currentUser?.shortname) {
        return [];
    }

    const response = await searchEntities(
        APPLICATIONS_SPACE,
        "/surveys",
        `@owner_shortname:${currentUser.shortname}`,
        100,
        0,
        "created_at",
        SortType.descending
    );
    return response.records || [];
}
