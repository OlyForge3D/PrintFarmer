import React from 'react';
import { Tabs } from '@/common/components/ui/Tabs';
import {
  type DurationTrend,
  type FailureReason,
  type MaterialSuccessRate,
  type PrinterMaterialPerformance,
  type TemperatureQualityCorrelation,
  useMaterialSuccessRates,
  usePrinterMaterialPerformance,
  useTemperatureQualityCorrelation,
  useDurationTrends,
  useFailureReasons,
} from '../hooks/useCorrelationAnalytics';
import { MaterialSuccessRateChart } from './MaterialSuccessRateChart';
import { PrinterMaterialHeatmap } from './PrinterMaterialHeatmap';
import { TemperatureScatterPlot } from './TemperatureScatterPlot';
import { DurationTrendChart } from './DurationTrendChart';
import { FailureReasonsChart } from './FailureReasonsChart';

interface Props {
  days?: number;
}

const EMPTY_MATERIAL_SUCCESS_RATES: MaterialSuccessRate[] = [];
const EMPTY_PRINTER_MATERIAL_PERFORMANCE: PrinterMaterialPerformance[] = [];
const EMPTY_TEMPERATURE_QUALITY_CORRELATION: TemperatureQualityCorrelation[] = [];
const EMPTY_DURATION_TRENDS: DurationTrend[] = [];
const EMPTY_FAILURE_REASONS: FailureReason[] = [];

export const CorrelationChartsSection = React.memo(function CorrelationChartsSection({ days }: Props) {
  return (
    <Tabs defaultTab="material">
      <Tabs.List>
        <Tabs.Tab id="material">Material Success</Tabs.Tab>
        <Tabs.Tab id="printer-material">Printer × Material</Tabs.Tab>
        <Tabs.Tab id="temperature">Temperature</Tabs.Tab>
        <Tabs.Tab id="duration">Duration Trends</Tabs.Tab>
        <Tabs.Tab id="failures">Failure Reasons</Tabs.Tab>
      </Tabs.List>
      <Tabs.Panels>
        <Tabs.Panel id="material">
          <MaterialPanel days={days} />
        </Tabs.Panel>
        <Tabs.Panel id="printer-material">
          <PrinterMaterialPanel days={days} />
        </Tabs.Panel>
        <Tabs.Panel id="temperature">
          <TemperaturePanel days={days} />
        </Tabs.Panel>
        <Tabs.Panel id="duration">
          <DurationPanel days={days} />
        </Tabs.Panel>
        <Tabs.Panel id="failures">
          <FailuresPanel days={days} />
        </Tabs.Panel>
      </Tabs.Panels>
    </Tabs>
  );
});

function MaterialPanel({ days }: Props) {
  const query = useMaterialSuccessRates(days);
  return <MaterialSuccessRateChart data={query.data ?? EMPTY_MATERIAL_SUCCESS_RATES} isLoading={query.isLoading} error={query.error} />;
}

function PrinterMaterialPanel({ days }: Props) {
  const query = usePrinterMaterialPerformance(days);
  return <PrinterMaterialHeatmap data={query.data ?? EMPTY_PRINTER_MATERIAL_PERFORMANCE} isLoading={query.isLoading} error={query.error} />;
}

function TemperaturePanel({ days }: Props) {
  const query = useTemperatureQualityCorrelation(days);
  return <TemperatureScatterPlot data={query.data ?? EMPTY_TEMPERATURE_QUALITY_CORRELATION} isLoading={query.isLoading} error={query.error} />;
}

function DurationPanel({ days }: Props) {
  const query = useDurationTrends(days);
  return <DurationTrendChart data={query.data ?? EMPTY_DURATION_TRENDS} isLoading={query.isLoading} error={query.error} />;
}

function FailuresPanel({ days }: Props) {
  const query = useFailureReasons(days);
  return <FailureReasonsChart data={query.data ?? EMPTY_FAILURE_REASONS} isLoading={query.isLoading} error={query.error} />;
}
