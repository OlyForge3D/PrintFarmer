import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock OpenTelemetry modules
vi.mock('@opentelemetry/sdk-trace-web', () => ({
  WebTracerProvider: class MockWebTracerProvider {
    register = vi.fn();
  },
  BatchSpanProcessor: class MockBatchSpanProcessor {},
  ConsoleSpanExporter: class MockConsoleSpanExporter {},
}));

vi.mock('@opentelemetry/auto-instrumentations-web', () => ({
  getWebAutoInstrumentations: vi.fn(() => []),
}));

vi.mock('@opentelemetry/resources', () => ({
  resourceFromAttributes: vi.fn((attrs) => attrs),
}));

vi.mock('@opentelemetry/exporter-trace-otlp-http', () => ({
  OTLPTraceExporter: class MockOTLPTraceExporter {},
}));

vi.mock('@opentelemetry/instrumentation', () => ({
  registerInstrumentations: vi.fn(),
}));

vi.mock('../services/unifiedLogging', () => ({}));

describe('telemetry config', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should initialize telemetry module', async () => {
    // Just test that the module can be imported without errors
    const { initializeTelemetry } = await import('../config');
    expect(typeof initializeTelemetry).toBe('function');
  });

  it('should create WebTracerProvider with correct resource attributes', async () => {
    const { resourceFromAttributes } = await import('@opentelemetry/resources');
    const { initializeTelemetry } = await import('../config');
    
    vi.clearAllMocks();
    initializeTelemetry();

    expect(resourceFromAttributes).toHaveBeenCalledWith(
      expect.objectContaining({
        'service.name': 'PrintFarmer.Frontend',
        'service.version': '1.0.0',
        'service.namespace': 'printfarmer',
      })
    );
  });
});
