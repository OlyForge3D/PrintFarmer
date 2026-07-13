/**
 * Presentational display of a single fallback chain (issue #718).
 *
 * Renders each member of the chain as a labelled row with:
 *  - Position number (1-based) and toolhead index / name.
 *  - A state chip (`Active`, `Backup ready`, `Empty`, `Exhausted`, `Mismatch`)
 *    with a matching MDI glyph so state is conveyed by shape/text — never by
 *    color alone (WCAG 1.4.1 non-color).
 *  - Current spool + material summary when known.
 *
 * The chain state is derived by `deriveFallbackGroupChainState`; this
 * component performs no data fetching so it is trivially testable and reused
 * across the panel and any future summary views.
 */
import { Badge, Button } from "@/common/components/ui";
import {
  AlertCircleIcon,
  CheckCircleIcon,
  InfoIcon,
  ArrowUpIcon,
  ArrowDownIcon,
} from "@/common/components/icons/MdiIcons";
import type {
  FallbackGroupChainState,
  FallbackMemberChainState,
  FallbackMemberState,
} from "@/features/fallback-groups/types";

interface StateAppearance {
  label: string;
  badgeVariant: "success" | "info" | "warning" | "error" | "default";
  icon: React.ReactNode;
}

function appearanceFor(state: FallbackMemberState): StateAppearance {
  switch (state) {
    case "active":
      return {
        label: "Active",
        badgeVariant: "success",
        icon: <CheckCircleIcon className="h-3.5 w-3.5" ariaLabel="Active" />,
      };
    case "backup":
      return {
        label: "Backup ready",
        badgeVariant: "info",
        icon: <ArrowDownIcon className="h-3.5 w-3.5" ariaLabel="Backup ready" />,
      };
    case "exhausted":
      return {
        label: "Exhausted",
        badgeVariant: "error",
        icon: <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Exhausted — spool ran out" />,
      };
    case "mismatch":
      return {
        label: "Wrong material",
        badgeVariant: "warning",
        icon: <AlertCircleIcon className="h-3.5 w-3.5" ariaLabel="Material mismatch" />,
      };
    case "empty":
    default:
      return {
        label: "Empty",
        badgeVariant: "default",
        icon: <InfoIcon className="h-3.5 w-3.5" ariaLabel="Empty — no spool loaded" />,
      };
  }
}

function memberLabel(entry: FallbackMemberChainState, index: number): string {
  const { member } = entry;
  const name = member.toolheadName?.trim();
  return name && name.length > 0
    ? `Position ${index + 1} — T${member.toolheadIndex} ${name}`
    : `Position ${index + 1} — T${member.toolheadIndex}`;
}

export interface FallbackChainDisplayProps {
  chain: FallbackGroupChainState;
  /**
   * Optional reorder handlers. When provided, per-row up/down buttons appear
   * for keyboard-accessible reordering. When omitted, the display is
   * read-only.
   */
  onMoveUp?: (memberIndex: number) => void;
  onMoveDown?: (memberIndex: number) => void;
  /** Optional disabled flag while a mutation is in flight. */
  disabled?: boolean;
}

export function FallbackChainDisplay({
  chain,
  onMoveUp,
  onMoveDown,
  disabled,
}: FallbackChainDisplayProps) {
  if (chain.members.length === 0) {
    return (
      <p className="text-xs text-pf-text-tertiary italic" data-testid="chain-empty">
        No toolheads in this chain.
      </p>
    );
  }

  const reorderable = Boolean(onMoveUp || onMoveDown);

  return (
    <ol
      className="space-y-1.5"
      data-testid="fallback-chain-display"
      aria-label={`Fallback chain for ${chain.group.name}`}
    >
      {chain.members.map((entry, index) => {
        const appearance = appearanceFor(entry.state);
        const canMoveUp = reorderable && index > 0;
        const canMoveDown = reorderable && index < chain.members.length - 1;
        return (
          <li
            key={entry.member.id || `${entry.member.toolheadId}-${index}`}
            className="flex items-start gap-2 rounded border border-pf-border/60 bg-pf-bg-0 px-2 py-1.5"
            data-member-state={entry.state}
          >
            <span
              aria-hidden="true"
              className="mt-0.5 inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-pf-border bg-pf-bg-1 text-[10px] font-mono font-medium text-pf-text-secondary"
            >
              {index + 1}
            </span>
            <div className="flex flex-1 flex-wrap items-center gap-x-2 gap-y-1">
              <span className="text-xs font-medium text-pf-text-primary">
                {memberLabel(entry, index)}
              </span>
              <Badge variant={appearance.badgeVariant} size="sm">
                <span className="inline-flex items-center gap-1">
                  {appearance.icon}
                  <span>{appearance.label}</span>
                </span>
              </Badge>
              <span className="text-xs text-pf-text-tertiary">
                {entry.member.currentSpoolId != null
                  ? `Spool #${entry.member.currentSpoolId}${entry.member.currentMaterial ? ` · ${entry.member.currentMaterial}` : ""}`
                  : "No spool loaded"}
              </span>
            </div>
            {reorderable && (
              <div className="flex items-center gap-1" role="group" aria-label={`Reorder ${memberLabel(entry, index)}`}>
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => onMoveUp?.(index)}
                  disabled={disabled || !canMoveUp}
                  aria-label={`Move ${memberLabel(entry, index)} up`}
                  className="p-1! h-auto!"
                  iconCenter={<ArrowUpIcon className="h-3.5 w-3.5" ariaLabel="" />}
                />
                <Button
                  variant="subtle"
                  size="sm"
                  onClick={() => onMoveDown?.(index)}
                  disabled={disabled || !canMoveDown}
                  aria-label={`Move ${memberLabel(entry, index)} down`}
                  className="p-1! h-auto!"
                  iconCenter={<ArrowDownIcon className="h-3.5 w-3.5" ariaLabel="" />}
                />
              </div>
            )}
          </li>
        );
      })}
    </ol>
  );
}
