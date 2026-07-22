import React from 'react';

interface SettingsMatchTextProps {
  text: string;
  query?: string;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export function SettingsMatchText({ text, query }: SettingsMatchTextProps) {
  const normalizedQuery = query?.trim();

  if (!normalizedQuery) {
    return <>{text}</>;
  }

  const pattern = new RegExp(`(${escapeRegExp(normalizedQuery)})`, 'ig');
  const parts = text.split(pattern);

  if (parts.length === 1) {
    return <>{text}</>;
  }

  const lowerQuery = normalizedQuery.toLowerCase();

  return (
    <>
      {parts.map((part, index) => (
        part.toLowerCase() === lowerQuery ? (
          <mark
            key={`${part}-${index}`}
            className="rounded-sm bg-pf-accent-bg/35 px-1 text-pf-text-primary"
          >
            {part}
          </mark>
        ) : (
          <React.Fragment key={`${part}-${index}`}>{part}</React.Fragment>
        )
      ))}
    </>
  );
}
