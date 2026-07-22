import React from 'react';
import clsx from 'clsx';

interface ProfileInheritanceTreeProps {
  profileName: string;
  parentChain: Array<{ name: string; id?: string }>;
  className?: string;
}

/**
 * Visual tree showing the inheritance chain of a profile
 * Displays: root ancestor → ... → parent → current profile
 */
export const ProfileInheritanceTree: React.FC<ProfileInheritanceTreeProps> = ({
  profileName,
  parentChain,
  className,
}) => {
  if (parentChain.length === 0) {
    return (
      <div className={clsx('py-2', className)}>
        <div className="px-3 py-2 rounded-lg bg-pf-bg-0 border-2 border-pf-accent">
          <div className="text-sm font-semibold text-pf-text-primary">{profileName}</div>
          <div className="text-xs text-pf-text-muted mt-0.5">Standalone profile</div>
        </div>
      </div>
    );
  }

  // Build chain: ancestors → current
  const chain = [...parentChain, { name: profileName }];

  return (
    <div className={clsx('py-2', className)}>
      <div className="space-y-2">
        {chain.map((node, index) => {
          const isLast = index === chain.length - 1;
          const isCurrent = isLast;

          return (
            <div key={index}>
              {/* Node */}
              <div
                className={clsx(
                  'px-3 py-2 rounded-lg border',
                  isCurrent
                    ? 'bg-pf-bg-0 border-2 border-pf-accent'
                    : 'bg-pf-bg-0 border border-pf-border'
                )}
              >
                <div
                  className={clsx(
                    'text-sm',
                    isCurrent ? 'font-semibold text-pf-text-primary' : 'text-pf-text-primary'
                  )}
                >
                  {node.name}
                </div>
                {index === 0 && chain.length > 1 && (
                  <div className="text-xs text-pf-text-muted mt-0.5">Root ancestor</div>
                )}
                {isCurrent && (
                  <div className="text-xs text-pf-text-muted mt-0.5">Current profile</div>
                )}
              </div>

              {/* Connector */}
              {!isLast && (
                <div className="flex items-center justify-center h-6">
                  <div className="w-0.5 h-full bg-pf-border" />
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};
