import type { QueueRecommendationDto } from "@/types/api";

const EMPTY_STATE_TEXT = "No actionable recommendations right now. Queue constraints are currently satisfied.";

const categoryLabels: Record<QueueRecommendationDto["category"], string> = {
  "material-mismatch": "Material",
  "nozzle-mismatch": "Nozzle",
  "bed-clear-blocking": "Bed Clear",
  "idle-printer-opportunity": "Idle Opportunity",
};

interface QueueRecommendationsPanelProps {
  recommendations: QueueRecommendationDto[];
  isLoading: boolean;
}

export function QueueRecommendationsPanel({ recommendations, isLoading }: QueueRecommendationsPanelProps) {
  return (
    <section className="bg-pf-bg-1 border border-pf-border rounded-lg p-4 mb-6">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-semibold text-pf-text-primary">Queue To-Do Recommendations</h2>
      </div>

      {isLoading ? (
        <p className="text-sm text-pf-text-secondary">Loading recommendations…</p>
      ) : recommendations.length === 0 ? (
        <p className="text-sm text-pf-text-secondary">{EMPTY_STATE_TEXT}</p>
      ) : (
        <ul className="space-y-3">
          {recommendations.map((recommendation) => (
            <li key={recommendation.category} className="border border-pf-border rounded-md p-3">
              <div className="flex items-center justify-between gap-3">
                <p className="text-sm font-medium text-pf-text-primary">{recommendation.title}</p>
                <span className="text-xs px-2 py-0.5 rounded bg-pf-accent-bg text-pf-accent">
                  +{recommendation.estimatedUnlockedJobCount} jobs
                </span>
              </div>
              <p className="text-sm text-pf-text-secondary mt-1">{recommendation.actionText}</p>
              <p className="text-xs text-pf-text-secondary mt-2 uppercase tracking-wide">
                {categoryLabels[recommendation.category]}
              </p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
