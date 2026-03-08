import { getStatusIndicatorColor } from '@/features/printers/utils/statusColors';

interface PrinterStatusHeaderProps {
  name: string;
  modelName?: string | null;
  state: string;
  isOnline: boolean;
  isPrinting: boolean;
  isPaused: boolean;
  isShutdown: boolean;
}

export function PrinterStatusHeader({
  name,
  modelName,
  state,
  isOnline,
  isPrinting,
  isPaused,
  isShutdown,
}: PrinterStatusHeaderProps) {
  const statusDotClasses = getStatusIndicatorColor({
    state,
    isOnline,
    isPrinting,
    isPaused,
    isShutdown,
  });

  const toCamelCase = (str: string): string => {
    return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
  };

  return (
    <div className="flex justify-between items-center mb-2 gap-2">
      <div className="flex-1 min-w-0">
        <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
          {name}
        </div>
        {modelName && (
          <div className="text-pf-text-secondary text-xs truncate">
            {modelName.trim()}
          </div>
        )}
      </div>

      <div className="inline-flex items-center gap-1.5 px-2 py-1 rounded-full text-xs font-medium shrink-0 bg-pf-bg-0/4 border border-white/10 text-pf-text-primary">
        <span className={`h-2 w-2 rounded-full ${statusDotClasses}`} aria-hidden />
        <span className="text-pf-text-secondary">
          {isOnline ? toCamelCase(state) : 'Offline'}
        </span>
      </div>
    </div>
  );
}
