import React, { useState, useEffect } from 'react';
import { useUnifiedLogging } from '../hooks/useUnifiedLogging';
import { LogEntry } from '../services/unifiedLogging';

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

  useEffect(() => {
    const updateLogs = () => {
      const allLogs = debugHelpers.getStoredLogs();
      setLogs(allLogs.slice(-maxEntries));
    };

    updateLogs();
    const interval = setInterval(updateLogs, refreshInterval);
    
    return () => clearInterval(interval);
  }, [debugHelpers, maxEntries, refreshInterval]);

  const filteredLogs = logs.filter(log => {
    if (filter !== 'all' && log.level !== filter) return false;
    if (searchTerm && !log.message.toLowerCase().includes(searchTerm.toLowerCase()) && 
        !log.component?.toLowerCase().includes(searchTerm.toLowerCase())) return false;
    return true;
  });

  const getLevelColor = (level: LogEntry['level']): string => {
    switch (level) {
      case 'error': return 'text-red-600 bg-red-50';
      case 'warn': return 'text-yellow-600 bg-yellow-50';
      case 'info': return 'text-blue-600 bg-blue-50';
      case 'debug': return 'text-gray-600 bg-gray-50';
      default: return 'text-gray-600 bg-gray-50';
    }
  };

  const handleDownloadLogs = () => {
    debugHelpers.downloadLogs();
    logger.logUserAction('download_logs', { totalLogs: logs.length });
  };

  const handleClearLogs = () => {
    debugHelpers.clearStoredLogs();
    setLogs([]);
    logger.logUserAction('clear_logs');
  };

  const handleTestLogs = () => {
    logger.debug('Test debug message', { testData: 'debug test' });
    logger.info('Test info message', { testData: 'info test' });
    logger.warn('Test warning message', { testData: 'warning test' });
    logger.error('Test error message', { testData: 'error test' });
    logger.logUserAction('test_logs_generated');
  };

  return (
    <div className="bg-white border border-gray-200 rounded-lg shadow-sm">
      {/* Header */}
      <div className="px-4 py-3 border-b border-gray-200">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-4">
            <button
              onClick={() => setIsExpanded(!isExpanded)}
              className="flex items-center space-x-2 text-gray-700 hover:text-gray-900"
            >
              <span className={`transform transition-transform ${isExpanded ? 'rotate-90' : ''}`}>
                ►
              </span>
              <h3 className="text-lg font-semibold">Unified Logging Dashboard</h3>
            </button>
            <span className="bg-blue-100 text-blue-800 text-xs font-medium px-2.5 py-0.5 rounded">
              {filteredLogs.length} / {logs.length} logs
            </span>
          </div>
          
          <div className="flex items-center space-x-2">
            <button
              onClick={handleTestLogs}
              className="px-3 py-1 text-xs font-medium text-purple-600 bg-purple-100 rounded hover:bg-purple-200"
            >
              Test Logs
            </button>
            <button
              onClick={handleDownloadLogs}
              className="px-3 py-1 text-xs font-medium text-green-600 bg-green-100 rounded hover:bg-green-200"
            >
              Download
            </button>
            <button
              onClick={handleClearLogs}
              className="px-3 py-1 text-xs font-medium text-red-600 bg-red-100 rounded hover:bg-red-200"
            >
              Clear
            </button>
          </div>
        </div>
      </div>

      {/* Expandable Content */}
      {isExpanded && (
        <div className="p-4">
          {/* Filters */}
          <div className="mb-4 flex flex-wrap items-center gap-3">
            <div className="flex items-center space-x-2">
              <label htmlFor="level-filter" className="text-sm font-medium text-gray-700">
                Level:
              </label>
              <select
                id="level-filter"
                value={filter}
                onChange={(e) => setFilter(e.target.value as typeof filter)}
                className="text-sm border border-gray-300 rounded px-2 py-1 focus:ring-2 focus:ring-blue-500 focus:border-transparent"
              >
                <option value="all">All</option>
                <option value="error">Error</option>
                <option value="warn">Warning</option>
                <option value="info">Info</option>
                <option value="debug">Debug</option>
              </select>
            </div>
            
            <div className="flex items-center space-x-2">
              <label htmlFor="search-logs" className="text-sm font-medium text-gray-700">
                Search:
              </label>
              <input
                id="search-logs"
                type="text"
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                placeholder="Search messages or components..."
                className="text-sm border border-gray-300 rounded px-2 py-1 w-64 focus:ring-2 focus:ring-blue-500 focus:border-transparent"
              />
            </div>
          </div>

          {/* Log Entries */}
          <div className="bg-gray-50 rounded border max-h-96 overflow-y-auto">
            {filteredLogs.length === 0 ? (
              <div className="p-4 text-center text-gray-500">
                No log entries found. {logs.length === 0 ? 'Try clicking "Test Logs" to generate some entries.' : 'Adjust your filters.'}
              </div>
            ) : (
              <div className="divide-y divide-gray-200">
                {filteredLogs.reverse().map((log, index) => (
                  <div key={`${log.timestamp.getTime()}-${index}`} className="p-3 hover:bg-gray-100">
                    <div className="flex items-start space-x-3">
                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${getLevelColor(log.level)}`}>
                        {log.level.toUpperCase()}
                      </span>
                      
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center space-x-2 text-xs text-gray-500 mb-1">
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
                        
                        <p className="text-sm text-gray-900 break-words">
                          {log.message}
                        </p>
                        
                        {log.context && (
                          <details className="mt-2">
                            <summary className="cursor-pointer text-xs text-gray-600 hover:text-gray-900">
                              View context
                            </summary>
                            <pre className="mt-1 text-xs text-gray-700 bg-white p-2 rounded border overflow-x-auto">
                              {JSON.stringify(log.context, null, 2)}
                            </pre>
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