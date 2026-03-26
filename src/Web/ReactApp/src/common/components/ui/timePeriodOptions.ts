export interface TimePeriodOption {
  readonly label: string;
  readonly value: number | undefined;
}

export const TIME_PERIOD_OPTIONS: readonly TimePeriodOption[] = [
  { label: '7 days', value: 7 },
  { label: '30 days', value: 30 },
  { label: '90 days', value: 90 },
  { label: '1 year', value: 365 },
  { label: 'All time', value: undefined },
] as const;

export type TimePeriodFilterValue =
  | { type: 'preset'; days: number | undefined }
  | { type: 'custom'; startDate: string; endDate: string };
