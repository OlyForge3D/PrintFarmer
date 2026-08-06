import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiClient } from "@/services/api";
import { PrinterBackend, type AutoDispatchDetailedStatus } from "@/types/api";

// Mock axios
vi.mock("axios", () => ({
  default: {
    create: vi.fn(() => ({
      get: vi.fn(),
      post: vi.fn(),
      put: vi.fn(),
      delete: vi.fn(),
      patch: vi.fn(),
      request: vi.fn(),
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    })),
  },
}));

describe("ApiClient", () => {
  let apiClient: ApiClient;

  beforeEach(() => {
    apiClient = new ApiClient();
  });

  describe("constructor", () => {
    it("should create an instance", () => {
      expect(apiClient).toBeDefined();
      expect(apiClient).toBeInstanceOf(ApiClient);
    });
  });

  describe("getPrinters", () => {
    it("should call the correct endpoint", async () => {
      const mockResponse = {
        data: [
          {
            id: "1",
            name: "Test Printer",
            serverUrl: "http://test.local",
            notes: "Test notes",
            isOnline: true,
            state: "idle",
            backend: PrinterBackend.Moonraker,
          },
        ],
      };

      // Mock the get method
      const mockGet = vi.fn().mockResolvedValue(mockResponse);
      // access internal axios client for mocking; cast to index signature
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get =
        mockGet;

      const result = await apiClient.getPrinters();

      // Updated endpoint now uses the faster summary list endpoint
      expect(mockGet).toHaveBeenCalledWith("/printers", { params: undefined });
      expect(result).toEqual(mockResponse.data);
    });
  });

  describe("getHealthStatus", () => {
    it("should call the health endpoint", async () => {
      const mockResponse = {
        data: { status: "ok" },
      };

      const mockGet = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get =
        mockGet;

      const result = await apiClient.getBasicHealth();

      expect(mockGet).toHaveBeenCalledWith("/healthz");
      expect(result).toEqual(mockResponse.data);
    });
  });

  describe("createPrinter", () => {
    it("should POST to the correct endpoint", async () => {
      const printerDto = {
        name: "New Printer",
        serverUrl: "http://new.local",
        backend: PrinterBackend.PrusaLink,
      };

      const mockResponse = {
        data: {
          id: "2",
          ...printerDto,
          isOnline: false,
          state: null,
        },
      };

      const mockPost = vi.fn().mockResolvedValue(mockResponse);
      (
        apiClient as unknown as { client: { post: typeof mockPost } }
      ).client.post = mockPost;

      const result = await apiClient.createPrinter(printerDto);

      expect(mockPost).toHaveBeenCalledWith("/printers", printerDto);
      expect(result).toEqual(mockResponse.data);
    });
  });

  describe("typed queue dispatch outcomes", () => {
    it.each([
      [200, "Accepted", "accepted"],
      [202, "Unknown", "reconciliation"],
      [409, "Rejected", "conflict"],
    ] as const)(
      "normalizes the production job body for HTTP %i/%s",
      async (status, outcome, expectedKind) => {
        const job = {
          id: "job-1",
          rowVersion: "next-etag",
          dispatchResult: {
            outcome,
            attemptId: "attempt-1",
            attemptNumber: 2,
            errorCode: outcome === "Rejected" ? "printer_busy" : undefined,
            errorDetail:
              outcome === "Unknown" ? "reconciliation required" : undefined,
          },
        };
        const mockPost = vi.fn().mockResolvedValue({ status, data: job });
        (
          apiClient as unknown as { client: { post: typeof mockPost } }
        ).client.post = mockPost;

        const result = await apiClient.dispatchPrintQueueJob(
          "job-1",
          "reviewed-etag"
        );

        expect(result.kind).toBe(expectedKind);
        expect(result.httpStatus).toBe(status);
        expect("job" in result ? result.job : undefined).toEqual(job);
        expect(mockPost).toHaveBeenCalledWith(
          "/job-queue/job-1/dispatch",
          undefined,
          expect.objectContaining({
            timeout: 0,
            headers: { "If-Match": '"reviewed-etag"' },
          })
        );
      }
    );

    it.each([
      [412, "stale"],
      [503, "unavailable"],
    ] as const)(
      "normalizes typed dispatch failure HTTP %i as %s",
      async (status, expectedKind) => {
        const mockPost = vi.fn().mockResolvedValue({
          status,
          data: {
            error:
              status === 412
                ? "dispatch_revision_conflict"
                : "dispatch_outcome_unavailable",
            detail: "review or retry",
          },
        });
        (
          apiClient as unknown as { client: { post: typeof mockPost } }
        ).client.post = mockPost;

        const result = await apiClient.dispatchPrintQueueJob(
          "job-1",
          "reviewed-etag"
        );

        expect(result.kind).toBe(expectedKind);
        expect(result.httpStatus).toBe(status);
        expect("errorCode" in result ? result.errorCode : undefined).toBe(
          status === 412
            ? "dispatch_revision_conflict"
            : "dispatch_outcome_unavailable"
        );
      }
    );
  });

  describe("reviewed printer mutation ETags", () => {
    it("sends displayed revisions for edit, maintenance, Z-offset, spool, and toolhead mutations", async () => {
      const put = vi.fn().mockResolvedValue({
        data: { id: "printer-1", success: true },
        headers: { etag: '"printer-v2"' },
      });
      const post = vi.fn().mockResolvedValue({
        data: { success: true },
        headers: { etag: '"printer-v2"' },
      });
      const del = vi.fn().mockResolvedValue({
        data: { success: true },
        headers: { etag: '"printer-v3"' },
      });
      (
        apiClient as unknown as {
          client: { put: typeof put; post: typeof post; delete: typeof del };
        }
      ).client.put = put;
      (
        apiClient as unknown as {
          client: { put: typeof put; post: typeof post; delete: typeof del };
        }
      ).client.post = post;
      (
        apiClient as unknown as {
          client: { put: typeof put; post: typeof post; delete: typeof del };
        }
      ).client.delete = del;

      await apiClient.updatePrinter(
        "printer-1",
        { name: "Reviewed" },
        "printer-v1"
      );
      await apiClient.setPrinterMaintenance(
        "printer-1",
        true,
        "printer-v1"
      );
      await apiClient.saveZOffset(
        "printer-1",
        { offsetMm: 0.1, saveToFirmware: false },
        "printer-v1"
      );
      expect(
        await apiClient.setActiveSpool(
          "printer-1",
          42,
          "printer-v1"
        )
      ).toBe("printer-v2");
      expect(
        await apiClient.setToolheadSpool(
          "printer-1",
          0,
          42,
          "printer-v1"
        )
      ).toBe("printer-v2");
      expect(
        await apiClient.clearToolheadSpool(
          "printer-1",
          0,
          "printer-v2"
        )
      ).toBe("printer-v3");

      for (const call of [...put.mock.calls, ...post.mock.calls]) {
        const config = call.at(-1) as { headers?: Record<string, string> };
        if (config?.headers?.["If-Match"]) {
          expect(config.headers["If-Match"]).toMatch(/^"printer-v1"$/);
        }
      }
      expect(del).toHaveBeenCalledWith(
        "/printers/printer-1/toolheads/0/spool",
        { headers: { "If-Match": '"printer-v2"' } }
      );
    });
  });

  describe("job scheduling wall-time contract", () => {
    it.each([
      ["scheduleJob", "post", "/job-scheduling/job-1/schedule"],
      ["rescheduleJob", "put", "/job-scheduling/job-1/reschedule"],
    ] as const)(
      "%s sends offset-free non-UTC wall time without conversion",
      async (method, verb, route) => {
        const request = {
          scheduledLocalTime: "2026-11-02T09:30:00",
          timeZone: "America/New_York",
          recurrencePattern: "Daily" as const,
          recurrenceInterval: 2,
          recurrenceEndLocalTime: "2026-11-10T09:30:00",
        };
        const transport = vi.fn().mockResolvedValue({
          data: {
            id: "schedule-1",
            jobId: "job-1",
            scheduledLocalTime: request.scheduledLocalTime,
            scheduledStartTimeUtc: "2026-11-02T14:30:00Z",
            timeZone: request.timeZone,
            recurrencePattern: request.recurrencePattern,
            recurrenceInterval: request.recurrenceInterval,
          },
        });
        (
          apiClient as unknown as {
            client: { post: typeof transport; put: typeof transport };
          }
        ).client[verb] = transport;

        await apiClient[method]("job-1", request);

        expect(transport).toHaveBeenCalledWith(route, request);
        expect(request.scheduledLocalTime).not.toMatch(/[zZ]|[+-]\d\d:\d\d$/);
      }
    );
  });

  describe("auto-dispatch endpoints", () => {
    it("should fetch global auto-dispatch status from the auto-dispatch route", async () => {
      const mockResponse = {
        data: {
          globalEnabled: true,
          printers: [],
        },
      };

      const mockGet = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get =
        mockGet;

      const result = await apiClient.getAutoDispatchStatus();

      expect(mockGet).toHaveBeenCalledWith("/auto-dispatch/status");
      expect(result).toEqual(mockResponse.data);
    });

    it("should fetch per-printer auto-dispatch status from the auto-dispatch route", async () => {
      const mockResponse = {
        data: {
          printerId: "printer-1",
          enabled: true,
          state: "PendingReady",
          queueDepth: 1,
        },
      };

      const mockGet = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get =
        mockGet;

      const result = await apiClient.getAutoDispatchPrinterStatus("printer-1");

      expect(mockGet).toHaveBeenCalledWith("/auto-dispatch/printer-1/status");
      expect(result).toEqual(mockResponse.data);
    });

    it("should post ready confirmations through the canonical auto-dispatch helper", async () => {
      const mockResponse = {
        data: {
          status: {
            printerId: "printer-1",
            enabled: true,
            state: "Ready",
            queueDepth: 1,
          },
          nextJob: null,
          filamentCheck: null,
        },
      };

      const mockPost = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      const result = await apiClient.confirmAutoDispatchReady(
        "printer-1",
        "dispatch-etag"
      );

      expect(mockPost).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/ready",
        undefined,
        expect.objectContaining({
          headers: { "If-Match": '"dispatch-etag"' },
          validateStatus: expect.any(Function),
        })
      );
      const config = mockPost.mock.calls[0]?.[2];
      expect(config.validateStatus(200)).toBe(true);
      expect(config.validateStatus(409)).toBe(true);
      expect(config.validateStatus(500)).toBe(false);
      expect(result).toEqual(mockResponse.data);
    });

    it("should make filament override confirmation explicit in the ready request", async () => {
      const mockResponse = {
        data: {
          status: {
            printerId: "printer-1",
            enabled: true,
            state: "Ready",
            queueDepth: 1,
          },
          dispatchInitiated: true,
          filamentOverrideApplied: true,
        },
      };
      const mockPost = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await apiClient.confirmAutoDispatchReady(
        "printer-1",
        "dispatch-etag",
        true,
        "job-etag",
        "filament-check-etag"
      );

      expect(mockPost).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/ready?confirmFilamentOverride=true",
        undefined,
        expect.objectContaining({
          headers: {
            "If-Match": '"dispatch-etag"',
            "X-Job-If-Match": '"job-etag"',
            "X-Filament-Check-If-Match": '"filament-check-etag"',
          },
        })
      );
    });

    it("should reject non-filament 409 responses as real conflicts", async () => {
      const mockPost = vi.fn().mockResolvedValue({
        status: 409,
        data: {
          error: "queue_empty",
          detail: "The reviewed queue head no longer exists.",
        },
      });
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await expect(
        apiClient.confirmAutoDispatchReady(
          "printer-1",
          "dispatch-etag",
          true,
          "job-etag",
          "filament-check-etag"
        )
      ).rejects.toMatchObject({
        statusCode: 409,
        message: "The reviewed queue head no longer exists.",
      });
    });

    it("should expose confirmAutoDispatchReady instead of the removed markPrinterReady alias", () => {
      expect(typeof apiClient.confirmAutoDispatchReady).toBe("function");
      expect((apiClient as unknown as { markPrinterReady?: unknown }).markPrinterReady).toBeUndefined();
    });

    it("should post skip requests to the auto-dispatch route", async () => {
      const mockPost = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await apiClient.skipAutoDispatchJob(
        "printer-1",
        "dispatch-etag",
        "job-etag"
      );

      expect(mockPost).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/skip",
        undefined,
        {
          headers: {
            "If-Match": '"dispatch-etag"',
            "X-Job-If-Match": '"job-etag"',
          },
        }
      );
    });

    it("should post cancel requests to the auto-dispatch route", async () => {
      const mockPost = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await apiClient.cancelAutoDispatch("printer-1", "dispatch-etag");

      expect(mockPost).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/cancel",
        undefined,
        { headers: { "If-Match": '"dispatch-etag"' } }
      );
    });

    it("should post pre-clear requests to the auto-dispatch route", async () => {
      const mockResponse = {
        data: {
          printerId: "printer-1",
          enabled: true,
          state: "None",
          queueDepth: 0,
          bedPreConfirmed: true,
        },
      };

      const mockPost = vi.fn().mockResolvedValue(mockResponse);
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      const result = await apiClient.preClearAutoDispatchBed(
        "printer-1",
        "dispatch-etag"
      );

      expect(mockPost).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/pre-clear",
        undefined,
        { headers: { "If-Match": '"dispatch-etag"' } }
      );
      expect(result).toEqual(mockResponse.data);
    });

    it("should put per-printer enabled changes to the auto-dispatch route", async () => {
      const mockPut = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { put: typeof mockPut } }).client.put =
        mockPut;

      await apiClient.setAutoDispatchEnabled(
        "printer-1",
        true,
        "dispatch-etag",
        "printer-etag"
      );

      expect(mockPut).toHaveBeenCalledWith(
        "/auto-dispatch/printer-1/enabled",
        { enabled: true },
        {
          headers: {
            "If-Match": '"dispatch-etag"',
            "X-Printer-If-Match": '"printer-etag"',
          },
        }
      );
    });

    it("should put global enabled changes to the auto-dispatch route", async () => {
      const mockPut = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { put: typeof mockPut } }).client.put =
        mockPut;

      const statuses: AutoDispatchDetailedStatus[] = [
        {
          printerId: "printer-1",
          printerName: "Printer 1",
          enabled: true,
          isReady: true,
          queueDepth: 1,
          readyGateChecks: [],
          state: "Ready",
          dispatchStateETag: "dispatch-etag",
          printerETag: "printer-etag",
        },
      ];
      await apiClient.setAutoDispatchGlobalEnabled(false, statuses);

      expect(mockPut).toHaveBeenCalledWith("/auto-dispatch/enabled", {
        enabled: false,
        expectedVersions: {
          "printer-1": {
            dispatchStateETag: "dispatch-etag",
            printerETag: "printer-etag",
          },
        },
      });
    });

    it.each([
      [200, "replayed"],
      [202, "accepted"],
      [409, "conflict"],
      [412, "stale"],
      [422, "incompatible"],
      [503, "unavailable"],
    ] as const)(
      "normalizes exact calibration acknowledgement HTTP %i as %s",
      async (status, expectedKind) => {
        const data =
          status === 200 || status === 202
            ? {
                message: "acknowledged",
                jobETag: "job-next",
                dispatchStateETag: "dispatch-next",
              }
            : {
                error: "typed_failure",
                detail: "retry requires reviewed state",
              };
        const mockPost = vi.fn().mockResolvedValue({ status, data });
        (
          apiClient as unknown as { client: { post: typeof mockPost } }
        ).client.post = mockPost;

        const result =
          await apiClient.acknowledgeCalibrationBedClearAndStart({
            jobId: "job-1",
            printerId: "printer-1",
            jobETag: "job-etag",
            dispatchStateETag: "dispatch-etag",
            expectedPrinterConfigRevision: 7,
            idempotencyKey: "stable-key",
          });

        expect(result.kind).toBe(expectedKind);
        expect(result.httpStatus).toBe(status);
        expect(mockPost).toHaveBeenCalledWith(
          "/job-queue/job-1/acknowledge-bed-clear-and-start",
          {
            printerId: "printer-1",
            expectedPrinterConfigRevision: 7,
          },
          expect.objectContaining({
            headers: {
              "Idempotency-Key": "stable-key",
              "If-Match": '"job-etag"',
              "X-Dispatch-State-If-Match": '"dispatch-etag"',
            },
          })
        );
      }
    );

    it.each([
      [200, "Accepted", "accepted"],
      [202, "Unknown", "reconciliation"],
      [409, "Rejected", "conflict"],
      [412, null, "stale"],
      [503, "InProgress", "unavailable"],
    ] as const)(
      "normalizes dispatch-to HTTP %i as %s",
      async (status, outcome, expectedKind) => {
        const job = outcome
          ? {
              id: "job-1",
              rowVersion: "job-v2",
              status: outcome === "Accepted" ? "Printing" : "Starting",
              dispatchResult: {
                attemptId: "attempt-2",
                attemptNumber: 2,
                outcome,
                errorCode: outcome === "Rejected" ? "printer_busy" : null,
                errorDetail: outcome === "Rejected" ? "Printer busy." : null,
                isRetryable: outcome === "Rejected",
                requiresReconciliation: outcome === "Unknown",
                jobRevision: "job-v2",
                dispatchStateRevision: "dispatch-v2",
              },
            }
          : undefined;
        const data =
          status === 412
            ? { error: "revision_conflict", detail: "Refresh required." }
            : status === 503
              ? { error: "dispatch_outcome_unavailable", job }
              : job;
        const mockPost = vi.fn().mockResolvedValue({ status, data });
        (
          apiClient as unknown as { client: { post: typeof mockPost } }
        ).client.post = mockPost;

        const result = await apiClient.dispatchJobToPrinter(
          "job-1",
          "printer-1",
          "job-v1"
        );

        expect(result.kind).toBe(expectedKind);
        expect(result.httpStatus).toBe(status);
        expect(mockPost).toHaveBeenCalledWith(
          "/job-queue/job-1/dispatch-to",
          { printerId: "printer-1" },
          expect.objectContaining({
            headers: { "If-Match": '"job-v1"' },
            validateStatus: expect.any(Function),
          })
        );
      }
    );

    it("uses only bounded typed maintenance routes", async () => {
      const mockPost = vi.fn().mockResolvedValue({
        data: { success: true },
      });
      (
        apiClient as unknown as { client: { post: typeof mockPost } }
      ).client.post = mockPost;

      await apiClient.extrudeFilament("printer-1", -5, 300);
      await apiClient.mmuGateAction("printer-1", {
        protocol: "Qidibox",
        action: "Eject",
        gateIndex: 2,
      });

      expect(mockPost).toHaveBeenNthCalledWith(
        1,
        "/printers/printer-1/extrude",
        { distanceMm: -5, feedrateMmPerMinute: 300 }
      );
      expect(mockPost).toHaveBeenNthCalledWith(
        2,
        "/printers/printer-1/mmu/gate-action",
        {
          protocol: "Qidibox",
          action: "Eject",
          gateIndex: 2,
        }
      );
      expect(
        mockPost.mock.calls.some(([url]) => String(url).endsWith("/gcode"))
      ).toBe(false);
    });

    describe("printables endpoints", () => {
      it("should fetch user collections from the printables collections endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "collection-1", name: "Favorites", modelCount: 12 }],
            nextCursor: "cursor-2",
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesUserCollections("ripley", { cursor: "cursor-1", limit: 8 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/users/ripley/collections", {
          params: { cursor: "cursor-1", limit: 8 },
        });
        expect(result).toEqual(mockResponse.data);
      });

      it("should normalize @-prefixed usernames for printables collections requests", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "collection-1", name: "Favorites", modelCount: 12 }],
            nextCursor: null,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        await apiClient.getPrintablesUserCollections("@ripley", { limit: 8 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/users/ripley/collections", {
          params: { cursor: undefined, limit: 8 },
        });
      });

      it("should fetch user models from the printables models endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "model-1", name: "Voron clip", authorHandle: "ripley", downloadCount: 12 }],
            nextCursor: null,
            hasMore: false,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesUserModels("ripley", { limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/users/ripley/models", {
          params: { cursor: undefined, limit: 24 },
        });
        expect(result).toEqual({
          items: [{ id: "model-1", title: "Voron clip", author: "ripley", slug: null, thumbnailUrl: null, likesCount: undefined, downloadsCount: 12, fileCount: undefined, sourceUrl: undefined }],
          nextCursor: null,
          hasMore: false,
        });
      });

      it("should normalize @-prefixed usernames for printables models requests", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "model-1", title: "Voron clip", author: "ripley" }],
            nextCursor: null,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        await apiClient.getPrintablesUserModels("@ripley", { limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/users/ripley/models", {
          params: { cursor: undefined, limit: 24 },
        });
      });

      it("should query printables search endpoint with keyword and offset pagination", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "result-1", name: "tool holder", authorHandle: "maker", downloadCount: 101 }],
            offset: 24,
            limit: 24,
            hasMore: true,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.searchPrintablesModels("tool holder", {
          offset: 24,
          limit: 24,
        });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/search", {
          params: { query: "tool holder", offset: 24, limit: 24 },
        });
        expect(result).toEqual({
          items: [{ id: "result-1", title: "tool holder", author: "maker", slug: null, thumbnailUrl: null, likesCount: undefined, downloadsCount: 101, fileCount: undefined, sourceUrl: undefined }],
          hasMore: true,
          offset: 24,
          limit: 24,
        });
      });

      it("should fetch collection models from the printables collection endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "model-1", name: "Collection model", authorHandle: "maker" }],
            nextCursor: "cursor-2",
            hasMore: true,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesCollectionModels("collection-1", { cursor: "cursor-1", limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/collections/collection-1/models", {
          params: { cursor: "cursor-1", limit: 24, query: undefined, ordering: undefined },
        });
        expect(result).toEqual({
          items: [{ id: "model-1", title: "Collection model", author: "maker", slug: null, thumbnailUrl: null, likesCount: undefined, downloadsCount: undefined, fileCount: undefined, sourceUrl: undefined }],
          nextCursor: "cursor-2",
          hasMore: true,
        });
      });

      it("should fetch printables oauth status", async () => {
        const mockResponse = {
          data: {
            isLinked: true,
            hasRefreshToken: true,
            scope: "public",
            linkedAtUtc: "2026-06-15T00:00:00Z",
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesOAuthStatus();

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/oauth/status");
        expect(result).toEqual(mockResponse.data);
      });

      it("should request printables oauth authorize url", async () => {
        const mockResponse = {
          data: {
            authorizationUrl: "https://account.prusa3d.com/o/authorize/?foo=bar",
          },
        };

        const mockPost = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { post: typeof mockPost } }).client.post = mockPost;

        const result = await apiClient.getPrintablesOAuthAuthorizeUrl();

        expect(mockPost).toHaveBeenCalledWith("/3d-models/printables/oauth/connect");
        expect(result).toEqual(mockResponse.data);
      });

      it("should complete printables oauth callback with code and state", async () => {
        const mockResponse = {
          data: {
            isLinked: true,
            hasRefreshToken: true,
            scope: "likes history",
            linkedAtUtc: "2026-06-15T00:00:00Z",
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.completePrintablesOAuthCallback("oauth-code", "oauth-state");

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/oauth/callback", {
          params: { code: "oauth-code", state: "oauth-state" },
        });
        expect(result).toEqual(mockResponse.data);
      });

      it("should disconnect printables oauth connection", async () => {
        const mockPost = vi.fn().mockResolvedValue({ data: undefined });
        (apiClient as unknown as { client: { post: typeof mockPost } }).client.post = mockPost;

        await apiClient.disconnectPrintablesOAuth();

        expect(mockPost).toHaveBeenCalledWith("/3d-models/printables/oauth/disconnect");
      });

      it("should fetch liked models from private printables endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "liked-1", name: "Liked model", authorHandle: "maker", downloadCount: 41 }],
            nextCursor: "cursor-2",
            hasMore: true,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesLikedModels({ cursor: "cursor-1", limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/liked", {
          params: { cursor: "cursor-1", limit: 24 },
        });
        expect(result).toEqual({
          items: [{ id: "liked-1", title: "Liked model", author: "maker", slug: null, thumbnailUrl: null, likesCount: undefined, downloadsCount: 41, fileCount: undefined, sourceUrl: undefined }],
          nextCursor: "cursor-2",
          hasMore: true,
        });
      });

      it("should fetch printables download history from private endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "history-1", name: "History model", authorHandle: "maker", downloadedAt: "2026-06-15T01:02:03Z" }],
            nextCursor: null,
            hasMore: false,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesDownloadHistory({ limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/history", {
          params: { cursor: undefined, limit: 24 },
        });
        expect(result).toEqual({
          items: [{ id: "history-1", title: "History model", author: "maker", slug: null, thumbnailUrl: null, likesCount: undefined, downloadsCount: undefined, fileCount: undefined, sourceUrl: undefined, downloadedAt: "2026-06-15T01:02:03Z" }],
          nextCursor: null,
          hasMore: false,
        });
      });

      describe("queue body ETags", () => {
        it("strips header quotes from bulk cancel body tokens", async () => {
          const mockGet = vi.fn();
          const mockPost = vi.fn().mockResolvedValue({ data: {} });
          (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;
          (apiClient as unknown as { client: { post: typeof mockPost } }).client.post = mockPost;

          await apiClient.bulkCancelJobs({
            jobs: [
              { jobId: "job-a", rowVersion: '"etag-a"' },
              { jobId: "job-b", rowVersion: 'W/"etag-b"' },
            ],
          });

          expect(mockPost).toHaveBeenCalledWith("/job-queue-analytics/bulk/cancel", {
            jobIds: ["job-a", "job-b"],
            jobETags: { "job-a": "etag-a", "job-b": "etag-b" },
          });
          expect(mockGet).not.toHaveBeenCalled();
        });

      });
    });
  });

  describe("getFilamentsPaged", () => {
    it("sends limit and offset as query params", async () => {
      const mockGet = vi.fn().mockResolvedValue({
        data: { items: [], totalCount: 0 },
      });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getFilamentsPaged({ limit: 50, offset: 100 });

      expect(mockGet).toHaveBeenCalledWith("/spoolman/filaments", {
        params: { limit: 50, offset: 100 },
        signal: undefined,
      });
    });

    it("sends sort, search, material, and vendor as query params", async () => {
      const mockGet = vi.fn().mockResolvedValue({
        data: { items: [], totalCount: 0 },
      });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getFilamentsPaged({
        sort: "name:asc",
        search: "PLA",
        material: "PLA",
        vendor: "Bambu",
      });

      expect(mockGet).toHaveBeenCalledWith("/spoolman/filaments", {
        params: { sort: "name:asc", search: "PLA", material: "PLA", vendor: "Bambu" },
        signal: undefined,
      });
    });

    it("returns items and totalCount from paginated server response", async () => {
      const serverItems = [
        { id: 1, name: "PLA Basic White", material: "PLA", vendor: "Bambu" },
        { id: 2, name: "PLA Basic Black", material: "PLA", vendor: "Bambu" },
      ];
      const mockGet = vi.fn().mockResolvedValue({
        data: { items: serverItems, totalCount: 250 },
      });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getFilamentsPaged({ limit: 2, offset: 0 });

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(250);
    });

    it("falls back to plain array response for backward compatibility", async () => {
      const serverItems = [
        { id: 1, name: "PETG Transparent", material: "PETG" },
      ];
      const mockGet = vi.fn().mockResolvedValue({ data: serverItems });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getFilamentsPaged();

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(1);
    });

    it("uses offset-aware totalCount fallback for plain array responses", async () => {
      const serverItems = [{ id: 1, name: "PETG Transparent", material: "PETG" }];
      const mockGet = vi.fn().mockResolvedValue({ data: serverItems });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getFilamentsPaged({ offset: 100 });

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(101);
    });

    it("passes AbortSignal through to the HTTP call", async () => {
      const controller = new AbortController();
      const mockGet = vi.fn().mockResolvedValue({ data: { items: [], totalCount: 0 } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getFilamentsPaged({ signal: controller.signal });

      expect(mockGet).toHaveBeenCalledWith(
        "/spoolman/filaments",
        expect.objectContaining({ signal: controller.signal }),
      );
    });

    it("omits zero offset from query params", async () => {
      const mockGet = vi.fn().mockResolvedValue({ data: { items: [], totalCount: 0 } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getFilamentsPaged({ limit: 50, offset: 0 });

      const callArgs = mockGet.mock.calls[0][1] as { params?: Record<string, unknown> };
      expect(callArgs?.params?.offset).toBeUndefined();
    });

    it("returns totalCount 0 when paginated response omits totalCount field", async () => {
      // Simulates a server that returns { items: [...] } without totalCount
      const serverItems = [{ id: 1, name: "PLA" }];
      const mockGet = vi.fn().mockResolvedValue({ data: { items: serverItems } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getFilamentsPaged();

      expect(result.items).toEqual(serverItems);
      // totalCount should fall back to 0 (not crash) when field is missing
      expect(result.totalCount).toBe(0);
    });

    it("returns empty items array when paginated response items field is not an array", async () => {
      const mockGet = vi.fn().mockResolvedValue({ data: { items: null, totalCount: 5 } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getFilamentsPaged();

      expect(result.items).toEqual([]);
      expect(result.totalCount).toBe(5);
    });
  });

  describe("getSpools", () => {
    it("falls back to plain array response for backward compatibility", async () => {
      const serverItems = [
        { id: 1, name: "Spool A", material: "PLA", remainingWeightG: 800 },
      ];
      const mockGet = vi.fn().mockResolvedValue({ data: serverItems });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getSpools();

      expect(result.items).toEqual(serverItems);
      // totalCount falls back to array length for legacy responses
      expect(result.totalCount).toBe(1);
    });

    it("uses offset-aware totalCount fallback for plain array responses", async () => {
      const serverItems = [{ id: 1, name: "Spool A", material: "PLA", remainingWeightG: 800 }];
      const mockGet = vi.fn().mockResolvedValue({ data: serverItems });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getSpools({ offset: 50 });

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(51);
    });

    it("returns items and totalCount from paginated server response", async () => {
      const serverItems = [{ id: 1, name: "Spool A", material: "PLA" }];
      const mockGet = vi.fn().mockResolvedValue({
        data: { items: serverItems, totalCount: 99 },
      });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getSpools({ limit: 1 });

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(99);
    });

    it("returns totalCount 0 when paginated response omits totalCount field", async () => {
      const serverItems = [{ id: 2, name: "Spool B" }];
      const mockGet = vi.fn().mockResolvedValue({ data: { items: serverItems } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      const result = await apiClient.getSpools();

      expect(result.items).toEqual(serverItems);
      expect(result.totalCount).toBe(0);
    });

    it("passes AbortSignal through to the HTTP call", async () => {
      const controller = new AbortController();
      const mockGet = vi.fn().mockResolvedValue({ data: { items: [], totalCount: 0 } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getSpools({ signal: controller.signal });

      expect(mockGet).toHaveBeenCalledWith(
        "/spoolman/spools",
        expect.objectContaining({ signal: controller.signal }),
      );
    });

    it("sends filter params as query params", async () => {
      const mockGet = vi.fn().mockResolvedValue({
        data: { items: [], totalCount: 0 },
      });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getSpools({
        limit: 25,
        offset: 50,
        sort: "remaining_weight:desc",
        search: "bambu",
        material: "PLA",
        vendor: "Bambu",
        location: "Box 1",
      });

      expect(mockGet).toHaveBeenCalledWith("/spoolman/spools", {
        params: {
          limit: 25,
          offset: 50,
          sort: "remaining_weight:desc",
          search: "bambu",
          material: "PLA",
          vendor: "Bambu",
          location: "Box 1",
        },
        signal: undefined,
      });
    });

    it("omits zero offset from query params", async () => {
      const mockGet = vi.fn().mockResolvedValue({ data: { items: [], totalCount: 0 } });
      (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

      await apiClient.getSpools({ limit: 50, offset: 0 });

      const callArgs = mockGet.mock.calls[0][1] as { params?: Record<string, unknown> };
      expect(callArgs?.params?.offset).toBeUndefined();
    });
  });
});
