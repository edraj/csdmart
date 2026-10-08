import { describe, expect, it } from "vitest";
import {
  convertArrayToObject,
  convertObjectToArray,
  transformFormToJson,
  transformJsonToForm,
} from "@shared/schema-editor-utils";

type Node = Record<string, unknown>;

describe("transformJsonToForm", () => {
  it("turns properties into an ordered array of named rows with ids", () => {
    const form = transformJsonToForm({
      type: "object",
      properties: { name: { type: "string" }, age: { type: "integer", title: "Age" } },
    }) as Node;
    expect(typeof form.id).toBe("string");
    const props = form.properties as Node[];
    expect(props.map((p) => p.name)).toEqual(["name", "age"]);
    // Every row gets editable title/description fields even when the schema has none.
    expect(props[0]).toMatchObject({ type: "string", title: "", description: "" });
    expect(props[1]).toMatchObject({ type: "integer", title: "Age", description: "" });
    expect(props.every((p) => typeof p.id === "string")).toBe(true);
  });

  it("does not mutate its input", () => {
    const input = { type: "object", properties: { a: { type: "string" } } };
    const copy = JSON.parse(JSON.stringify(input));
    transformJsonToForm(input);
    expect(input).toEqual(copy);
  });

  it("marks a null property as removed instead of crashing", () => {
    const form = transformJsonToForm({ type: "object", properties: { gone: null } }) as Node;
    expect(form.properties).toEqual([{ name: "gone", __removed: true }]);
  });

  it("passes primitives through", () => {
    expect(transformJsonToForm("x")).toBe("x");
    expect(transformJsonToForm(null)).toBe(null);
  });
});

describe("transformFormToJson", () => {
  it("is the inverse of transformJsonToForm for a plain object schema", () => {
    const schema = {
      type: "object",
      required: ["name"],
      properties: { name: { type: "string", title: "", description: "" } },
    };
    const back = transformFormToJson(transformJsonToForm(schema));
    expect(back).toEqual(schema);
  });

  it("strips form ids and writes removed rows back as null", () => {
    const out = transformFormToJson({
      id: "root",
      type: "object",
      properties: [
        { id: "1", name: "keep", type: "string" },
        { id: "2", name: "drop", __removed: true },
        null,
      ],
    }) as Node;
    expect(out.id).toBeUndefined();
    expect(out.properties).toEqual({ keep: { type: "string" }, drop: null });
  });

  it("collapses a single-property array item to that property's type", () => {
    const out = transformFormToJson({
      type: "array",
      items: { properties: [{ name: "only", type: "number" }] },
    }) as Node;
    expect((out.items as Node).type).toBe("number");
  });

  it("types a multi-property array item as a closed object", () => {
    const out = transformFormToJson({
      type: "array",
      items: { properties: [{ name: "a", type: "string" }, { name: "b", type: "string" }] },
    }) as Node;
    expect(out.items).toMatchObject({ type: "object", additionalProperties: false });
  });
});

describe("convertArrayToObject / convertObjectToArray", () => {
  it("pass non-collections through untouched", () => {
    expect(convertArrayToObject("x")).toBe("x");
    expect(convertObjectToArray(null)).toBe(null);
    expect(convertObjectToArray([1])).toEqual([1]);
  });

  it("skip the id key when listing an object", () => {
    expect(convertObjectToArray({ id: "x", a: { type: "string" } })).toEqual([
      { name: "a", type: "string", title: "", description: "" },
    ]);
  });
});
