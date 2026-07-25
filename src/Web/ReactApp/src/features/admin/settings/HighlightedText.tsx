import { Fragment } from 'react';
import { splitOnQuery } from './search-utils';

interface HighlightedTextProps {
  text: string;
  query: string;
}

/**
 * Renders `text` with case-insensitive matches of `query` wrapped in `<mark>`.
 * The original casing of `text` is preserved. An empty `query` renders the
 * text unchanged so the callsite does not have to add its own conditional.
 */
export function HighlightedText({ text, query }: HighlightedTextProps) {
  const segments = splitOnQuery(text, query);
  return (
    <>
      {segments.map((segment, idx) => (
        <Fragment key={idx}>
          {segment.matched ? (
            <mark className="rounded-sm bg-pf-accent/25 px-0.5 text-pf-text-primary">
              {segment.text}
            </mark>
          ) : (
            segment.text
          )}
        </Fragment>
      ))}
    </>
  );
}
