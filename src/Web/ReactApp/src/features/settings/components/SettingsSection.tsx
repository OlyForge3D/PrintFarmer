interface SettingsSectionProps {
  title?: string;
  description?: string;
  children: React.ReactNode;
}

export const SettingsSection: React.FC<SettingsSectionProps> = ({
  title,
  description,
  children,
}) => {
  return (
    <section className="py-4">
      {title && (
        <div className="mb-4">
          <h2 className="text-lg font-medium text-pf-text-primary">{title}</h2>
          {description && (
            <p className="mt-1 text-sm text-pf-text-secondary">{description}</p>
          )}
        </div>
      )}
      <div>{children}</div>
    </section>
  );
};
