import React, { useState, useEffect, useMemo, useCallback } from 'react';
import { useUnifiedLogging } from '../hooks/useUnifiedLogging';
import { LogEntry } from '../services/unifiedLogging';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button, Select } from '@/components/ui';

export interface UnifiedLoggingDashboardProps {
  maxEntries?: number;
  refreshInterval?: number;
}

export const UnifiedLoggingDashboard: React.FC<UnifiedLoggingDashboardProps> = ({
  maxEntries = 500,
  refreshInterval = 2000
}) => {
  const { debugHelpers, logger } = useUnifiedLogging({ component: 'UnifiedLoggingDashboard' });
  const [logs, setLogs] = useState<LogEntry[]>([]);
  const [isExpanded, setIsExpanded] = useState(false);
  const [filter, setFilter] = useState<'all' | 'error' | 'warn' | 'info' | 'debug'>('all');
  const [searchTerm, setSearchTerm] = useState('');

  // Stable function references to prevent infinite re-renders
  // Use the debugHelpers directly without creating additional callback wrappers
  const getStoredLogs = debugHelpers.getStoredLogs;
  const clearStoredLogs = debugHelpers.clearStoredLogs;
  const downloadLogs = debugHelpers.downloadLogs;

  useEffect(() => {
    const updateLogs = () => {
      const allLogs = getStoredLogs();
      // Fix timestamp parsing - convert string timestamps back to Date objects
      const processedLogs = allLogs.slice(-maxEntries).map(log => ({
        ...log,
        timestamp: typeof log.timestamp === 'string' ? new Date(log.timestamp) : log.timestamp
      }));
      setLogs(processedLogs);
    };

    updateLogs();
    const interval = setInterval(updateLogs, refreshInterval);
    
    return () => clearInterval(interval);
  }, [getStoredLogs, maxEntries, refreshInterval]);

  const filteredLogs = useMemo(() => {
    return logs.filter(log => {
      if (filter !== 'all' && log.level !== filter) return false;
      if (searchTerm && !log.message.toLowerCase().includes(searchTerm.toLowerCase()) && 
          !log.component?.toLowerCase().includes(searchTerm.toLowerCase())) return false;
      return true;
    });
  }, [logs, filter, searchTerm]);

  const getLevelColor = (level: LogEntry['level']): string => {
    switch (level) {
      case 'error': return 'text-pf-error bg-pf-error-bg';
      case 'warn': return 'text-pf-warning bg-pf-bg-2';
      case 'info': return 'text-pf-accent bg-pf-bg-2';
      case 'debug': return 'text-pf-text-tertiary bg-pf-bg-2';
      default: return 'text-pf-text-tertiary bg-pf-bg-2';
    }
  };

  const handleDownloadLogs = useCallback(() => {
    downloadLogs();
    logger.logUserAction('download_logs', { totalLogs: logs.length });
  }, [downloadLogs, logger, logs.length]);

  const handleClearLogs = useCallback(() => {
    clearStoredLogs();
    setLogs([]);
    logger.logUserAction('clear_logs');
  }, [clearStoredLogs, logger]);

  const handleTestLogs = useCallback(() => {
    logger.debug('Test debug message', { testData: 'debug test' });
    logger.info('Test info message', { testData: 'info test' });
    logger.warn('Test warning message', { testData: 'warning test' });
    logger.error('Test error message', { testData: 'error test' });
    logger.logUserAction('test_logs_generated');
  }, [logger]);

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-lg shadow-sm">
      {/* Header */}
      <div className="px-4 py-3 border-b border-pf-border">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-4">
            <Button
              type="button"
              onClick={() => setIsExpanded(!isExpanded)}
              variant="subtle"
              size="sm"
              className="!p-0 !h-auto"
            >
              <span className={`transform transition-transform ${isExpanded ? 'rotate-90' : ''}`}>
                ►
              </span>
              <h3 className="text-lg font-semibold ml-2">Unified Logging Dashboard</h3>
            </Button>
            <span className="bg-pf-bg-2 text-pf-accent text-xs font-medium px-2.5 py-0.5 rounded">
              {filteredLogs.length} / {logs.length} logs
            </span>
          </div>
          
          <div className="flex items-center space-x-2">
            <Button
              type="button"
              onClick={handleTestLogs}
              variant="secondary"
              size="sm"
            >
              Test Logs
            </Button>
            <Button
              type="button"
              onClick={handleDownloadLogs}
              variant="secondary"
              size="sm"
            >
              Download
            </Button>
            <Button
              type="button"
              onClick={handleClearLogs}
              variant="danger"
              size="sm"
            >
              Clear
            </Button>
          </div>
        </div>
      </div>

      {/* Expandable Content */}
      {isExpanded && (
        <div className="p-4">
          {/* Filters */}
          <div className="mb-4 flex flex-wrap items-center gap-3">
            <div className="flex items-center space-x-2">
              <label htmlFor="level-filter" className="text-sm font-medium text-pf-text-primary">
                Level:
              </label>
              <Select
                id="level-filter"
                value={filter}
                onChange={(e) => setFilter(e.target.value as typeof filter)}
              >
                <option value="all">All</option>
                <option value="error">Error</option>
                <option value="warn">Warning</option>
                <option value="info">Info</option>
                <option value="debug">Debug</option>
              </Select>
            </div>
            
            <div className="flex items-center space-x-2">
              <label htmlFor="search-logs" className="text-sm font-medium text-pf-text-primary">
                Search:
              </label>
              {/* eslint-disable-next-line local/pf-no-raw-html-controls */}
              <input
                id="search-logs"
                type="text"
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                placeholder="Search messages or components..."
                className="text-sm border border-pf-border rounded px-2 py-1 w-64 bg-pf-bg-0 text-pf-text-primary placeholder-pf-text-tertiary focus:ring-2 focus:ring-pf-accent focus:border-transparent"
              />
            </div>
          </div>

          {/* Log Entries */}
          <div className="bg-pf-bg-0 rounded border border-pf-border max-h-96 overflow-y-auto">
            {filteredLogs.length === 0 ? (
              <div className="p-4 text-center text-pf-text-tertiary">
                No log entries found. {logs.length === 0 ? 'Try clicking "Test Logs" to generate some entries.' : 'Adjust your filters.'}
              </div>
            ) : (
              <div className="divide-y divide-pf-border">
                {filteredLogs.reverse().map((log, index) => (
                  <div key={`${log.timestamp.getTime()}-${index}`} className="p-3 hover:bg-pf-bg-2">
                    <div className="flex items-start space-x-3">
                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${getLevelColor(log.level)}`}>
                        {log.level.toUpperCase()}
                      </span>
                      
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center space-x-2 text-xs text-pf-text-tertiary mb-1">
                          <span>{log.timestamp.toLocaleTimeString()}</span>
                          {log.component && (
                            <>
                              <span>•</span>
                              <span className="font-medium">{log.component}</span>
                            </>
                          )}
                          {log.userId && (
                            <>
                              <span>•</span>
                              <span>User: {log.userId}</span>
                            </>
                          )}
                        </div>
                        
                        <p className="text-sm text-pf-text-primary break-words">
                          {log.message}
                        </p>
                        {log.context != null && (
                          <details className="mt-2">
                            <summary className="cursor-pointer text-xs text-pf-text-secondary hover:text-pf-text-primary">View context</summary>
                            {/** Use a central helper to safely render unknown payloads */}
                            {renderUnknown(log.context)}
                          </details>
                        )}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

export default UnifiedLoggingDashboard;