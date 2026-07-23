import type { ThreeMfMetadata } from '@/types/models';
import { Card } from '@/common/components/ui/Card';
import { Badge } from '@/common/components/ui/Badge';
import { Button } from '@/common/components/ui/Button';

interface ThreeMfMetadataPanelProps {
  metadata: ThreeMfMetadata | null | undefined;
  autoTags?: string[];
  existingTagNames?: string[];
  onAcceptTag?: (tagName: string) => void;
}

export function ThreeMfMetadataPanel({ metadata, autoTags, existingTagNames = [], onAcceptTag }: ThreeMfMetadataPanelProps) {
  if (!metadata) {
    return null;
  }

  const hasAnyField = metadata.title || metadata.designer || metadata.description ||
    metadata.application || metadata.creationDate || metadata.materials.length > 0;

  if (!hasAnyField) {
    return null;
  }

  const pendingTags = (autoTags ?? metadata.autoTags ?? []).filter(
    (tag) => !existingTagNames.some((existing) => existing.toLowerCase() === tag.toLowerCase())
  );

  return (
    <Card>
      <Card.Header>
        <h3 className="text-sm font-semibold text-pf-text-primary">3MF Metadata</h3>
      </Card.Header>
      <Card.Body>
        <div className="space-y-2 text-sm">
          {metadata.title && (
            <MetadataRow label="Title" value={metadata.title} />
          )}
          {metadata.designer && (
            <MetadataRow label="Designer" value={metadata.designer} />
          )}
          {metadata.description && (
            <MetadataRow label="Description" value={metadata.description} />
          )}
          {metadata.application && (
            <MetadataRow label="Application" value={metadata.application} />
          )}
          {metadata.creationDate && (
            <MetadataRow label="Created" value={metadata.creationDate} />
          )}
          {metadata.materials.length > 0 && (
            <div className="flex items-start gap-2">
              <span className="text-pf-text-secondary min-w-[80px] shrink-0">Materials</span>
              <div className="flex flex-wrap gap-1">
                {metadata.materials.map((material) => (
                  <Badge key={material} variant="primary" size="sm">
                    {material}
                  </Badge>
                ))}
              </div>
            </div>
          )}
          {pendingTags.length > 0 && (
            <div className="mt-3 pt-3 border-t border-pf-border">
              <span className="text-xs text-pf-text-secondary font-medium uppercase tracking-wider">
                Suggested Tags
              </span>
              <div className="flex flex-wrap gap-1.5 mt-1.5">
                {pendingTags.map((tag) => (
                  <Button
                    key={tag}
                    variant="unstyled"
                    onClick={() => onAcceptTag?.(tag)}
                    className="inline-flex items-center gap-1 rounded-full bg-pf-accent-bg/10 px-2.5 py-0.5 text-xs font-medium text-pf-accent hover:bg-pf-accent-bg/20 transition-colors cursor-pointer border border-pf-accent/30"
                    title={`Apply tag "${tag}"`}
                  >
                    + {tag}
                  </Button>
                ))}
              </div>
            </div>
          )}
        </div>
      </Card.Body>
    </Card>
  );
}

function MetadataRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-start gap-2">
      <span className="text-pf-text-secondary min-w-[80px] shrink-0">{label}</span>
      <span className="text-pf-text-primary">{value}</span>
    </div>
  );
}
