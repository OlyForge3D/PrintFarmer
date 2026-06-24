import type { QueueRecommendationDto } from "@/types/api";
import clsx from "clsx";
import { CheckCircle2, Clock3, Layers3, Wrench } from "lucide-react";
import type { ComponentType } from "react";

const EMPTY_STATE_TEXT = "No actionable recommendations right now. Queue constraints are currently satisfied.";

const categoryLabels: Record<QueueRecommendationDto["category"], string> = {
  "material-mismatch": "Material",
  "nozzle-mismatch": "Nozzle",
  "bed-clear-blocking": "Bed Clear",
  "idle-printer-opportunity": "Idle Opportunity",
};

const categoryIcons: Record<QueueRecommendationDto["category"], ComponentType<{ className?: string }>> = {
  "material-mismatch": Layers3,
  "nozzle-mismatch": Wrench,
  "bed-clear-blocking": CheckCircle2,
  "idle-printer-opportunity": Clock3,
};

interface QueueRecommendationsPanelProps {
  recommendations: QueueRecommendationDto[];
  isLoading: boolean;
}

export function QueueRecommendationsPanel({ recommendations, isLoading }: QueueRecommendationsPanelProps) {
  return (
    <section className="bg-pf-bg-1/95 border border-pf-border rounded-xl p-4 mb-6 shadow-[0_14px_28px_rgba(0,0,0,0.16)] backdrop-blur-sm">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-semibold text-pf-text-primary tracking-wide">Queue To-Do Recommendations</h2>
      </div>

      {isLoading ? (
        <p className="text-sm text-pf-text-secondary animate-pulse">Loading recommendations…</p>
      ) : recommendations.length === 0 ? (
        <p className="text-sm text-pf-text-secondary">{EMPTY_STATE_TEXT}</p>
      ) : (
        <ul className="space-y-3">
          {recommendations.map((recommendation) => (
            <li
              key={recommendation.category}
              className="border border-pf-border rounded-lg p-3 bg-gradient-to-br from-pf-bg-0/45 to-transparent motion-safe:hover:-translate-y-px transition-all duration-200"
            >
              <div className="flex items-center justify-between gap-3">
                <p className="text-sm font-medium text-pf-text-primary flex items-center gap-2">
                  {(() => {
                    const Icon = categoryIcons[recommendation.category];
                    return <Icon className="h-4 w-4 text-pf-accent" aria-hidden="true" />;
                  })()}
                  {recommendation.title}
                </p>
                <span className="text-xs px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent">
                  +{recommendation.estimatedUnlockedJobCount} jobs
                </span>
              </div>
              <p className="text-sm text-pf-text-secondary mt-1">{recommendation.actionText}</p>
              <p
                className={clsx(
                  "text-xs mt-2 uppercase tracking-wide font-medium",
                  recommendation.priorityScore >= 75 ? "text-pf-warning" : "text-pf-text-secondary",
                )}
              >
                {categoryLabels[recommendation.category]}
              </p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
