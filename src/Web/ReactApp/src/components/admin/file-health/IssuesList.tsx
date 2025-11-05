import React, { useState } from 'react';
import { FileIssuesSummaryDto } from '@/types/api';

interface IssuesListProps {
  data: FileIssuesSummaryDto;
  isLoading: boolean;
}

export function IssuesList({ data, isLoading }: IssuesListProps) {
  const [selectedTab, setSelectedTab] = useState<'missing' | 'corrupted' | 'inaccessible'>('missing');

  if (isLoading) {
    return <div>Loading issues...</div>;
  }

  const hasAnyIssues =
    data.missingFiles.length > 0 ||
    data.corruptedFiles.length > 0 ||
    data.inaccessibleFiles.length > 0;

  if (!hasAnyIssues) {
    return (
      <div className="bg-pf-surface rounded-lg border border-pf-border p-6">
        <h3 className="text-lg font-semibold text-pf-text mb-4">File Issues</h3>
        <div className="bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg p-4">
          <p className="text-green-800 dark:text-green-200">No file issues detected! All files are in good standing.</p>
        </div>
      </div>
    );
  }

  const tabs = [
    {
      id: 'missing' as const,
      label: 'Missing Files',
      count: data.missingFiles.length,
      items: data.missingFiles,
      color: 'text-red-600 dark:text-red-400',
      bgColor: 'bg-red-50 dark:bg-red-900/20',
      borderColor: 'border-red-200 dark:border-red-800',
    },
    {
      id: 'corrupted' as const,
      label: 'Corrupted Files',
      count: data.corruptedFiles.length,
      items: data.corruptedFiles,
      color: 'text-orange-600 dark:text-orange-400',
      bgColor: 'bg-orange-50 dark:bg-orange-900/20',
      borderColor: 'border-orange-200 dark:border-orange-800',
    },
    {
      id: 'inaccessible' as const,
      label: 'Inaccessible Files',
      count: data.inaccessibleFiles.length,
      items: data.inaccessibleFiles,
      color: 'text-purple-600 dark:text-purple-400',
      bgColor: 'bg-purple-50 dark:bg-purple-900/20',
      borderColor: 'border-purple-200 dark:border-purple-800',
    },
  ];

  const activeTab = tabs.find((tab) => tab.id === selectedTab) || tabs[0];

  return (
    <div className="bg-pf-surface rounded-lg border border-pf-border p-6">
      <div className="flex items-center justify-between mb-6">
        <h3 className="text-lg font-semibold text-pf-text">File Issues</h3>
        <span className="text-sm text-pf-text-secondary">
          Total Issues: <span className="font-bold text-pf-accent">{data.totalIssues}</span>
        </span>
      </div>

      {/* Tab buttons */}
      <div className="flex gap-2 mb-6 border-b border-pf-border">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            onClick={() => setSelectedTab(tab.id)}
            className={`px-4 py-2 font-medium text-sm transition-colors relative ${
              selectedTab === tab.id
                ? `${tab.color} border-b-2 border-pf-accent`
                : 'text-pf-text-secondary hover:text-pf-text'
            }`}
          >
            {tab.label}
            {tab.count > 0 && (
              <span className="ml-2 bg-red-100 dark:bg-red-900/30 text-red-800 dark:text-red-200 text-xs font-bold rounded-full w-5 h-5 flex items-center justify-center">
                {tab.count}
              </span>
            )}
          </button>
        ))}
      </div>

      {/* Tab content */}
      <div className={`${activeTab.bgColor} border ${activeTab.borderColor} rounded-lg p-4`}>
        {activeTab.items.length === 0 ? (
          <p className="text-pf-text-secondary">No {activeTab.label.toLowerCase()} found.</p>
        ) : (
          <div className="space-y-3">
            {activeTab.items.map((item, idx) => (
              <div key={idx} className="flex items-start gap-3 p-2 hover:bg-pf-hover rounded transition-colors">
                <div className={`flex-shrink-0 w-2 h-2 rounded-full mt-1.5 ${activeTab.color}`} />
                <div className="flex-1 min-w-0">
                  <div className="flex items-start justify-between gap-2">
                    <div className="flex-1">
                      <p className={`font-medium ${activeTab.color} break-words`}>{item.fileName}</p>
                      <p className="text-xs text-pf-text-secondary mt-1">{item.fileType} file</p>
                      {selectedTab === 'missing' && 'lastHealthCheckDate' in item && item.lastHealthCheckDate && (
                        <p className="text-xs text-pf-text-secondary mt-1">
                          Last checked: {new Date(item.lastHealthCheckDate).toLocaleDateString()}
                        </p>
                      )}
                      {selectedTab === 'corrupted' && 'lastVerificationResult' in item && item.lastVerificationResult && (
                        <p className="text-xs text-pf-text-secondary mt-1">
                          Details: {item.lastVerificationResult}
                        </p>
                      )}
                      {selectedTab === 'inaccessible' && 'lastHealthCheckDate' in item && item.lastHealthCheckDate && (
                        <p className="text-xs text-pf-text-secondary mt-1">
                          Last checked: {new Date(item.lastHealthCheckDate).toLocaleDateString()}
                        </p>
                      )}
                    </div>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {activeTab.items.length > 0 && (
        <div className="mt-4 p-3 bg-yellow-50 dark:bg-yellow-900/20 border border-yellow-200 dark:border-yellow-800 rounded-lg">
          <p className="text-sm text-yellow-800 dark:text-yellow-200">
            <span className="font-semibold">Action Required:</span> Review and resolve these file issues to maintain data integrity.
          </p>
        </div>
      )}
    </div>
  );
}
