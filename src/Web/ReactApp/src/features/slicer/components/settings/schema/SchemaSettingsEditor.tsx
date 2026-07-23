import { useState, useMemo } from 'react';
import clsx from 'clsx';
import { Spinner, Tabs, TabList, Tab, TabPanels, TabPanel } from '@/common/components/ui';
import { useProfileSchema } from './useProfileSchema';
import { SchemaCategoryPanel } from './SchemaCategoryPanel';

interface SchemaSettingsEditorProps {
  profileType: 'process' | 'machine' | 'filament';
  values: Record<string, unknown>;
  onChange: (values: Record<string, unknown>) => void;
  disabled?: boolean;
  originalValues?: Record<string, unknown>;
  className?: string;
}

const CATEGORY_LABELS: Record<string, string> = {
  quality: 'Quality',
  strength: 'Strength',
  speed: 'Speed',
  support: 'Support & Adhesion',
  adhesion: 'Adhesion',
  temperature: 'Temperature',
  other: 'Other',
  general: 'General',
  buildVolume: 'Build Volume',
  extruder: 'Extruder',
  retraction: 'Retraction',
  bed: 'Bed',
  gcode: 'G-code',
  motion: 'Motion',
  flow: 'Flow',
  cooling: 'Cooling',
  physical: 'Physical Properties',
};

export function SchemaSettingsEditor({
  profileType,
  values,
  onChange,
  disabled = false,
  originalValues,
  className,
}: SchemaSettingsEditorProps) {
  const { data: schema, isLoading, error } = useProfileSchema(profileType);
  const [activeTab, setActiveTab] = useState(0);

  const handleFieldChange = (key: string, value: unknown) => {
    onChange({
      ...values,
      [key]: value,
    });
  };

  const hasChanges = (key: string): boolean => {
    if (!originalValues) return false;
    return originalValues[key] !== values[key];
  };

  const handleReset = (key: string) => {
    if (!originalValues) return;
    onChange({
      ...values,
      [key]: originalValues[key],
    });
  };

  const getOriginalValue = (key: string): unknown => {
    return originalValues?.[key];
  };

  const categories = useMemo(() => {
    if (!schema) return [];
    return schema.categories.filter(cat => {
      const categoryFields = schema.fields.filter(f => f.category === cat);
      return categoryFields.length > 0;
    });
  }, [schema]);

  if (isLoading) {
    return (
      <div className={clsx('flex items-center justify-center p-8', className)}>
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className={clsx('p-4 text-pf-error', className)}>
        Failed to load schema: {String(error)}
      </div>
    );
  }

  if (!schema) {
    return (
      <div className={clsx('p-4 text-pf-text-secondary', className)}>
        No schema available
      </div>
    );
  }

  if (categories.length === 0) {
    return (
      <div className={clsx('p-4 text-pf-text-secondary', className)}>
        No settings available for this profile type
      </div>
    );
  }

  return (
    <div className={clsx('space-y-4', className)}>
      <Tabs selectedIndex={activeTab} onChange={setActiveTab}>
        <TabList>
          {categories.map(cat => (
            <Tab key={cat}>{CATEGORY_LABELS[cat] || cat}</Tab>
          ))}
        </TabList>

        <TabPanels>
          {categories.map(cat => (
            <TabPanel key={cat}>
              <SchemaCategoryPanel
                category={cat}
                categoryLabel={CATEGORY_LABELS[cat] || cat}
                fields={schema.fields}
                values={values}
                onChange={handleFieldChange}
                disabled={disabled}
                hasChanges={hasChanges}
                onReset={handleReset}
                getOriginalValue={getOriginalValue}
              />
            </TabPanel>
          ))}
        </TabPanels>
      </Tabs>
    </div>
  );
}
