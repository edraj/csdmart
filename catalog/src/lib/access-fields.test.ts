import { describe, expect, it } from "vitest";
import {
  allowedValuesForField,
  constrainEnumOptions,
  isFieldRestricted,
  visibleColumns,
} from "./access-fields";
import type { PermissionsMap } from "./access";

const perms: PermissionsMap = {
  "management:/users:user": {
    allowed_actions: ["view", "update"],
    restricted_fields: ["email", "msisdn"],
    allowed_fields_values: { type: ["web", "mobile"], language: "en" },
  },
};

describe("isFieldRestricted", () => {
  it("hides listed fields, allows others", () => {
    expect(isFieldRestricted(perms, "email", "management", "/users", "user")).toBe(true);
    expect(isFieldRestricted(perms, "displayname", "management", "/users", "user")).toBe(false);
  });

  it("defaults to ALLOW when no permission entry matches", () => {
    expect(isFieldRestricted({}, "email", "management", "/users", "user")).toBe(false);
    expect(isFieldRestricted(perms, "email", "other", "/x", "content")).toBe(false);
  });
});

describe("allowedValuesForField", () => {
  it("returns the whitelist (array) and wraps scalars", () => {
    expect(allowedValuesForField(perms, "type", "management", "/users", "user")).toEqual(["web", "mobile"]);
    expect(allowedValuesForField(perms, "language", "management", "/users", "user")).toEqual(["en"]);
  });

  it("returns null when unconstrained or no entry", () => {
    expect(allowedValuesForField(perms, "displayname", "management", "/users", "user")).toBeNull();
    expect(allowedValuesForField({}, "type", "management", "/users", "user")).toBeNull();
  });
});

describe("visibleColumns", () => {
  const cols = [
    { key: "shortname" },
    { key: "email" }, // bare match
    { key: "attributes.msisdn" }, // dotted match -> bare "msisdn" restricted
    { key: "displayname" },
  ];

  it("drops restricted columns (bare and dotted) and keeps the rest", () => {
    const out = visibleColumns(cols, perms, "management", "/users", "user");
    expect(out.map((c) => c.key)).toEqual(["shortname", "displayname"]);
  });

  it("returns all columns when no entry or no restricted_fields", () => {
    expect(visibleColumns(cols, {}, "management", "/users", "user")).toHaveLength(4);
    const noRestrict: PermissionsMap = { "s:p:content": { allowed_actions: ["view"] } };
    expect(visibleColumns(cols, noRestrict, "s", "p", "content")).toHaveLength(4);
  });
});

describe("constrainEnumOptions", () => {
  it("narrows options to the whitelist", () => {
    expect(
      constrainEnumOptions(["web", "mobile", "api"], perms, "type", "management", "/users", "user"),
    ).toEqual(["web", "mobile"]);
  });

  it("keeps the current value even if outside the whitelist (no silent drop)", () => {
    expect(
      constrainEnumOptions(["web", "mobile", "legacy"], perms, "type", "management", "/users", "user", "legacy"),
    ).toEqual(["web", "mobile", "legacy"]);
  });

  it("returns options unchanged when unconstrained", () => {
    expect(
      constrainEnumOptions(["a", "b"], perms, "status", "management", "/users", "user"),
    ).toEqual(["a", "b"]);
  });
});
