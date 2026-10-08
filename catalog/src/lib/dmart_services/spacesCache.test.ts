import { describe, expect, it, vi } from "vitest";
import { DmartScope, type ApiQueryResponse } from "@edraj/tsdmart";
import { createSpacesCache } from "./spacesCache";

function response(names: string[]): ApiQueryResponse {
  return {
    status: "success",
    records: names.map((shortname) => ({ shortname, attributes: {} })),
  } as unknown as ApiQueryResponse;
}

describe("createSpacesCache", () => {
  it("fetches once per scope and serves the same promise afterwards", async () => {
    const fetch = vi.fn(async (scope: DmartScope) => response([`${scope}-a`]));
    const cache = createSpacesCache(fetch);

    const first = cache.get(DmartScope.managed);
    const second = cache.get(DmartScope.managed);
    expect(second).toBe(first);
    await first;
    await cache.get(DmartScope.public);

    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls.map((c) => c[0])).toEqual([DmartScope.managed, DmartScope.public]);
    expect(cache.size()).toBe(2);
  });

  it("fetches again after invalidate()", async () => {
    const fetch = vi.fn(async () => response(["a"]));
    const cache = createSpacesCache(fetch);

    await cache.get(DmartScope.managed);
    cache.invalidate();
    expect(cache.size()).toBe(0);
    await cache.get(DmartScope.managed);

    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("fetches again once the TTL has passed", async () => {
    let clock = 0;
    const fetch = vi.fn(async () => response(["a"]));
    const cache = createSpacesCache(fetch, 1000, () => clock);

    await cache.get(DmartScope.managed);
    clock = 999;
    await cache.get(DmartScope.managed);
    expect(fetch).toHaveBeenCalledTimes(1);

    clock = 1000;
    await cache.get(DmartScope.managed);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("does not keep a failed lookup", async () => {
    const fetch = vi
      .fn<(scope: DmartScope) => Promise<ApiQueryResponse>>()
      .mockRejectedValueOnce(new Error("offline"))
      .mockResolvedValueOnce(response(["a"]));
    const cache = createSpacesCache(fetch);

    await expect(cache.get(DmartScope.managed)).rejects.toThrow("offline");
    expect(cache.size()).toBe(0);
    await expect(cache.get(DmartScope.managed)).resolves.toMatchObject({ status: "success" });
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
