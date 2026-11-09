export function validateCatalog(catalog: unknown): string[];
export function validateApprovalDocument(approvals: unknown, catalog: unknown, scenarioIds: string[]): string[];
export function assertCleanSourceCommit(input: {
  sourceCommit?: string;
  workingTreeStatus: string;
}): void;
export function assertApprovedGolden(input: {
  approval?: {
    status?: string;
    approvedBy?: string;
    approvedAt?: string;
    approvedSourceSha?: string;
    baselineSha256?: string;
    sourceInventorySha256?: string;
    renderProfileId?: string;
    baseline?: string;
  };
  sourceSha256: string;
  sourceCommit: string;
  renderProfileId: string;
  baselineExists: boolean;
  baselineSha256?: string;
}): void;
