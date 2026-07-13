import { describe, it, expect, vi, beforeEach } from "vitest";

const hoisted = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  apiPut: vi.fn(),
  apiDelete: vi.fn(),
}));

vi.mock("@/services/api", () => ({
  apiClient: {
    get: hoisted.apiGet,
    post: hoisted.apiPost,
    put: hoisted.apiPut,
    delete: hoisted.apiDelete,
  },
}));

import { fallbackGroupsService } from "../service";

describe("fallbackGroupsService", () => {
  beforeEach(() => {
    hoisted.apiGet.mockReset();
    hoisted.apiPost.mockReset();
    hoisted.apiPut.mockReset();
    hoisted.apiDelete.mockReset();
  });

  it("list encodes the printer id and decodes the payload", async () => {
    hoisted.apiGet.mockResolvedValueOnce({
      data: [
        {
          id: "g1",
          printerId: "printer/1",
          name: "PLA",
          materialType: "PLA",
          displayOrder: 0,
          createdAt: "",
          updatedAt: "",
          members: [],
        },
      ],
    });
    const groups = await fallbackGroupsService.list("printer/1");
    expect(hoisted.apiGet).toHaveBeenCalledWith(
      "/printers/printer%2F1/fallback-groups",
      expect.objectContaining({}),
    );
    expect(groups).toHaveLength(1);
    expect(groups[0].id).toBe("g1");
  });

  it("get targets the single-group resource", async () => {
    hoisted.apiGet.mockResolvedValueOnce({ data: { id: "g", printerId: "p", members: [] } });
    await fallbackGroupsService.get("p", "g id with space");
    expect(hoisted.apiGet).toHaveBeenCalledWith(
      "/printers/p/fallback-groups/g%20id%20with%20space",
      expect.objectContaining({}),
    );
  });

  it("create POSTs to the collection root and decodes the response", async () => {
    hoisted.apiPost.mockResolvedValueOnce({ data: { id: "new", members: [] } });
    const g = await fallbackGroupsService.create("p", {
      name: "PLA",
      materialType: "PLA",
      toolheadIds: ["t1", "t2"],
    });
    expect(hoisted.apiPost).toHaveBeenCalledWith("/printers/p/fallback-groups", {
      name: "PLA",
      materialType: "PLA",
      toolheadIds: ["t1", "t2"],
    });
    expect(g.id).toBe("new");
  });

  it("update PUTs to the group resource", async () => {
    hoisted.apiPut.mockResolvedValueOnce({ data: { id: "g", members: [] } });
    await fallbackGroupsService.update("p", "g", {
      name: "PLA v2",
      materialType: "PLA",
      toolheadIds: ["t1"],
    });
    expect(hoisted.apiPut).toHaveBeenCalledWith("/printers/p/fallback-groups/g", {
      name: "PLA v2",
      materialType: "PLA",
      toolheadIds: ["t1"],
    });
  });

  it("remove DELETEs the group resource", async () => {
    hoisted.apiDelete.mockResolvedValueOnce({ data: undefined });
    await fallbackGroupsService.remove("p", "g");
    expect(hoisted.apiDelete).toHaveBeenCalledWith("/printers/p/fallback-groups/g");
  });

  it("propagates ApiError-shaped rejections unchanged", async () => {
    const err = { statusCode: 400, message: "Name already exists" };
    hoisted.apiPost.mockRejectedValueOnce(err);
    await expect(
      fallbackGroupsService.create("p", { name: "dup", materialType: "PLA", toolheadIds: ["t1"] }),
    ).rejects.toBe(err);
  });
});
