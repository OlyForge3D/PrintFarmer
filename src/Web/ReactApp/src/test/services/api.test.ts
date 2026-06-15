import { describe, it, expect, vi, beforeEach } from "vitest";
import { ApiClient } from "@/services/api";
import { PrinterBackend } from "@/types/api";

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

      const result = await apiClient.confirmAutoDispatchReady("printer-1");

      expect(mockPost).toHaveBeenCalledWith("/auto-dispatch/printer-1/ready");
      expect(result).toEqual(mockResponse.data);
    });

    it("should expose confirmAutoDispatchReady instead of the removed markPrinterReady alias", () => {
      expect(typeof apiClient.confirmAutoDispatchReady).toBe("function");
      expect((apiClient as unknown as { markPrinterReady?: unknown }).markPrinterReady).toBeUndefined();
    });

    it("should post skip requests to the auto-dispatch route", async () => {
      const mockPost = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await apiClient.skipAutoDispatchJob("printer-1");

      expect(mockPost).toHaveBeenCalledWith("/auto-dispatch/printer-1/skip");
    });

    it("should post cancel requests to the auto-dispatch route", async () => {
      const mockPost = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { post: typeof mockPost } }).client.post =
        mockPost;

      await apiClient.cancelAutoDispatch("printer-1");

      expect(mockPost).toHaveBeenCalledWith("/auto-dispatch/printer-1/cancel");
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

      const result = await apiClient.preClearAutoDispatchBed("printer-1");

      expect(mockPost).toHaveBeenCalledWith("/auto-dispatch/printer-1/pre-clear");
      expect(result).toEqual(mockResponse.data);
    });

    it("should put per-printer enabled changes to the auto-dispatch route", async () => {
      const mockPut = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { put: typeof mockPut } }).client.put =
        mockPut;

      await apiClient.setAutoDispatchEnabled("printer-1", true);

      expect(mockPut).toHaveBeenCalledWith("/auto-dispatch/printer-1/enabled", { enabled: true });
    });

    it("should put global enabled changes to the auto-dispatch route", async () => {
      const mockPut = vi.fn().mockResolvedValue({ data: undefined });
      (apiClient as unknown as { client: { put: typeof mockPut } }).client.put =
        mockPut;

      await apiClient.setAutoDispatchGlobalEnabled(false);

      expect(mockPut).toHaveBeenCalledWith("/auto-dispatch/enabled", { enabled: false });
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
            items: [{ id: "model-1", title: "Voron clip", author: "ripley" }],
            nextCursor: null,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesUserModels("ripley", { limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/users/ripley/models", {
          params: { cursor: undefined, limit: 24 },
        });
        expect(result).toEqual(mockResponse.data);
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

      it("should query printables search endpoint with keyword and cursor", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "result-1", title: "tool holder", author: "maker" }],
            nextCursor: "cursor-2",
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.searchPrintablesModels("tool holder", {
          cursor: "cursor-1",
          limit: 24,
        });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/search", {
          params: { query: "tool holder", cursor: "cursor-1", limit: 24 },
        });
        expect(result).toEqual(mockResponse.data);
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

      it("should disconnect printables oauth connection", async () => {
        const mockPost = vi.fn().mockResolvedValue({ data: undefined });
        (apiClient as unknown as { client: { post: typeof mockPost } }).client.post = mockPost;

        await apiClient.disconnectPrintablesOAuth();

        expect(mockPost).toHaveBeenCalledWith("/3d-models/printables/oauth/disconnect");
      });

      it("should fetch liked models from private printables endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "liked-1", title: "Liked model", author: "maker" }],
            nextCursor: "cursor-2",
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesLikedModels({ cursor: "cursor-1", limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/liked", {
          params: { cursor: "cursor-1", limit: 24 },
        });
        expect(result).toEqual(mockResponse.data);
      });

      it("should fetch printables download history from private endpoint", async () => {
        const mockResponse = {
          data: {
            items: [{ id: "history-1", title: "History model", author: "maker" }],
            nextCursor: null,
          },
        };

        const mockGet = vi.fn().mockResolvedValue(mockResponse);
        (apiClient as unknown as { client: { get: typeof mockGet } }).client.get = mockGet;

        const result = await apiClient.getPrintablesDownloadHistory({ limit: 24 });

        expect(mockGet).toHaveBeenCalledWith("/3d-models/printables/history", {
          params: { cursor: undefined, limit: 24 },
        });
        expect(result).toEqual(mockResponse.data);
      });
    });
  });
});
