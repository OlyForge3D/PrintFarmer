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
  const materialRates = useMaterialSuccessRates(days);
  const printerMaterial = usePrinterMaterialPerformance(days);
  const tempQuality = useTemperatureQualityCorrelation(days);
  const durationTrends = useDurationTrends(days);
  const failureReasons = useFailureReasons(days);

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
          <MaterialSuccessRateChart
            data={materialRates.data ?? EMPTY_MATERIAL_SUCCESS_RATES}
            isLoading={materialRates.isLoading}
            error={materialRates.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="printer-material">
          <PrinterMaterialHeatmap
            data={printerMaterial.data ?? EMPTY_PRINTER_MATERIAL_PERFORMANCE}
            isLoading={printerMaterial.isLoading}
            error={printerMaterial.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="temperature">
          <TemperatureScatterPlot
            data={tempQuality.data ?? EMPTY_TEMPERATURE_QUALITY_CORRELATION}
            isLoading={tempQuality.isLoading}
            error={tempQuality.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="duration">
          <DurationTrendChart
            data={durationTrends.data ?? EMPTY_DURATION_TRENDS}
            isLoading={durationTrends.isLoading}
            error={durationTrends.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="failures">
          <FailureReasonsChart
            data={failureReasons.data ?? EMPTY_FAILURE_REASONS}
            isLoading={failureReasons.isLoading}
            error={failureReasons.error}
          />
        </Tabs.Panel>
      </Tabs.Panels>
    </Tabs>
  );
});
