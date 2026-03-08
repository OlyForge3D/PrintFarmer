import React from 'react';
import { Tabs } from '@/common/components/ui/Tabs';
import {
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

export const CorrelationChartsSection: React.FC<Props> = ({ days }) => {
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
            data={materialRates.data ?? []}
            isLoading={materialRates.isLoading}
            error={materialRates.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="printer-material">
          <PrinterMaterialHeatmap
            data={printerMaterial.data ?? []}
            isLoading={printerMaterial.isLoading}
            error={printerMaterial.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="temperature">
          <TemperatureScatterPlot
            data={tempQuality.data ?? []}
            isLoading={tempQuality.isLoading}
            error={tempQuality.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="duration">
          <DurationTrendChart
            data={durationTrends.data ?? []}
            isLoading={durationTrends.isLoading}
            error={durationTrends.error}
          />
        </Tabs.Panel>
        <Tabs.Panel id="failures">
          <FailureReasonsChart
            data={failureReasons.data ?? []}
            isLoading={failureReasons.isLoading}
            error={failureReasons.error}
          />
        </Tabs.Panel>
      </Tabs.Panels>
    </Tabs>
  );
};
