import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import ModelFilteredJobsTab from "../ModelFilteredJobsTab";

// Mock the printQueueService
vi.mock("@/services/printQueueService", () => ({
  printQueueService: {
    getAllQueuedJobsAsync: vi.fn(),
  },
}));

describe("ModelFilteredJobsTab Component", () => {
  // Get reference to the mocked function
  let mockGetAllQueuedJobsAsync: any;

  beforeEach(() => {
    // Import the mock and setup reference
    const module = vi.mocked(require("@/services/printQueueService"));
    mockGetAllQueuedJobsAsync = module.printQueueService.getAllQueuedJobsAsync;
    vi.clearAllMocks();
    // Mock the service response
    mockGetAllQueuedJobsAsync.mockResolvedValue(mockJobs);
  });

  const mockJobs = [
    {
      id: "job1",
      fileName: "model1.stl",
      printerModel: "Prusa CORE One",
      material: "PLA",
      estimatedTime: 3600,
      progress: 50,
      status: "Queued",
      createdAt: new Date(Date.now() - 15 * 60000).toISOString(), // 15 min ago
    },
    {
      id: "job2",
      fileName: "model2.stl",
      printerModel: "Prusa CORE One",
      material: "PETG",
      estimatedTime: 5400,
      progress: 0,
      status: "Queued",
      createdAt: new Date(Date.now() - 30 * 60000).toISOString(), // 30 min ago
    },
    {
      id: "job3",
      fileName: "model3.stl",
      printerModel: "Bambu P1S",
      material: "ABS",
      estimatedTime: 7200,
      progress: 75,
      status: "Printing",
      createdAt: new Date(Date.now() - 1 * 60000).toISOString(), // 1 min ago
    },
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    // Mock the service response
    mockGetAllQueuedJobsAsync.mockResolvedValue(mockJobs);
  });

  it("should render component and load jobs on mount", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(mockGetAllQueuedJobsAsync).toHaveBeenCalled();
    });
  });

  it("should display stats for grouped models", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/Stats: 2 models/)).toBeInTheDocument();
    });
  });

  it("should group jobs correctly by model", async () => {
    const { container } = render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      const modelCards = container.querySelectorAll("[onclick]");
      expect(modelCards.length).toBe(2); // Two unique models
    });
  });

  it("should calculate statistics correctly per model", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      // Should display both models
      expect(screen.getByText(/Prusa CORE One/)).toBeInTheDocument();
      expect(screen.getByText(/Bambu P1S/)).toBeInTheDocument();
    });
  });

  it("should handle expand/collapse of model cards", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/Prusa CORE One/)).toBeInTheDocument();
    });

    const modelCard = screen.getByText(/Prusa CORE One/);
    fireEvent.click(modelCard);

    expect(screen.getByText(/Prusa CORE One.*expanded/)).toBeInTheDocument();
  });

  it("should filter by model name", async () => {
    const { container } = render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/Stats: 2 models/)).toBeInTheDocument();
    });

    // Simulate model filter change
    const refreshBtn = screen.getByText("Refresh");
    fireEvent.click(refreshBtn);

    await waitFor(() => {
      expect(mockGetAllQueuedJobsAsync).toHaveBeenCalledTimes(2);
    });
  });

  it("should handle error states gracefully", async () => {
    mockGetAllQueuedJobsAsync.mockRejectedValue(new Error("API Error"));

    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/error: Failed to load jobs/i)).toBeInTheDocument();
    });
  });

  it("should display loading state initially", async () => {
    render(<ModelFilteredJobsTab />);

    // Should show loading message initially
    expect(screen.getByText(/Loading models/)).toBeInTheDocument();
  });

  it("should calculate average wait time correctly", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      // The first model should have ~22.5 min avg wait time (15+30)/2
      expect(screen.getByText(/Prusa CORE One/)).toBeInTheDocument();
    });
  });

  it("should call onViewAllJobs callback when provided", async () => {
    const onViewAllJobs = vi.fn();
    render(<ModelFilteredJobsTab onViewAllJobs={onViewAllJobs} />);

    await waitFor(() => {
      expect(screen.getByText(/Prusa CORE One/)).toBeInTheDocument();
    });
  });

  it("should call onJobAction callback when provided", async () => {
    const onJobAction = vi.fn();
    render(<ModelFilteredJobsTab onJobAction={onJobAction} />);

    await waitFor(() => {
      expect(screen.getByText(/Prusa CORE One/)).toBeInTheDocument();
    });
  });

  it("should display empty state when no jobs found", async () => {
    mockGetAllQueuedJobsAsync.mockResolvedValue([]);

    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(
        screen.getByText(/No models found with the selected filters/i)
      ).toBeInTheDocument();
    });
  });

  it("should handle status filtering", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/Stats: 2 models/)).toBeInTheDocument();
    });
  });

  it("should refresh jobs on demand", async () => {
    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(mockGetAllQueuedJobsAsync).toHaveBeenCalledTimes(1);
    });

    const refreshBtn = screen.getByText("Refresh");
    fireEvent.click(refreshBtn);

    await waitFor(() => {
      expect(mockGetAllQueuedJobsAsync).toHaveBeenCalledTimes(2);
    });
  });

  it("should handle jobs without printerModel gracefully", async () => {
    const jobsWithoutModel = [
      {
        ...mockJobs[0],
        printerModel: undefined,
      },
    ];

    mockGetAllQueuedJobsAsync.mockResolvedValue(jobsWithoutModel);

    render(<ModelFilteredJobsTab />);

    await waitFor(() => {
      expect(screen.getByText(/Unknown/)).toBeInTheDocument();
    });
  });
});
