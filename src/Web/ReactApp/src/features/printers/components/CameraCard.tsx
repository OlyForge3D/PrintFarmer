import React, { useState } from 'react';
import { Printer } from '@/types/api';
import { CameraIcon, ImageIcon, VideoIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

interface CameraCardProps {
  printer: Printer;
}

/**
 * CameraCard - Displays a printer's camera feed in a card format
 * Used in the "Camera View" mode on the Printers page
 */
export function CameraCard({
  printer: p,
}: CameraCardProps) {
  const [imageError, setImageError] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('stream');
  const isOnline = p.isOnline ?? false;
  const state = p.state ?? '';
  const isPrinting = state.toLowerCase().includes('printing');

  // Camera URL handling
  const cameraSnapshotUrl = p.cameraSnapshotUrl;
  const cameraStreamUrl = p.cameraStreamUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);
  const hasSnapshot = !!cameraSnapshotUrl;
  const hasStream = !!cameraStreamUrl;

  // Determine which URL to show
  const activeUrl = cameraMode === 'stream' && hasStream 
    ? cameraStreamUrl 
    : cameraMode === 'snapshot' && hasSnapshot
    ? cameraSnapshotUrl
    : hasStream 
    ? cameraStreamUrl 
    : cameraSnapshotUrl;

  return (
    <div className="rounded-xl shadow-lg backdrop-blur-xl bg-pf-bg-0/5 border border-white/10 hover:border-white/20 transition-colors overflow-hidden flex flex-col min-h-0">
      {/* Camera feed - main content */}
      <div className="relative w-full aspect-video bg-pf-bg-2">
        {hasCameraUrls && !imageError ? (
          <img
            src={activeUrl ?? ''}
            alt={`${p.name} camera feed`}
            className="absolute inset-0 w-full h-full object-cover"
            loading="lazy"
            onError={() => setImageError(true)}
          />
        ) : (
          <div className="absolute inset-0 flex flex-col items-center justify-center text-pf-text-tertiary p-4">
            <CameraIcon className="w-12 h-12 mb-2 opacity-30" />
            <span className="text-sm">{hasCameraUrls ? 'Camera unavailable' : 'No camera configured'}</span>
          </div>
        )}
        
        {/* Status overlay - top right */}
        <div className="absolute top-2 right-2 flex gap-1">
          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium backdrop-blur-xs ${
            isOnline 
              ? 'bg-pf-status-online-bg/80 text-pf-status-online-text' 
              : 'bg-pf-border-medium/80 text-pf-text-secondary'
          }`}>
            {isOnline ? 'Online' : 'Offline'}
          </span>
          {isPrinting && (
            <span className="inline-flex items-center px-2 py-0.5 rounded-sm text-xs font-medium bg-pf-warning/80 text-pf-text-primary backdrop-blur-xs">
              Printing
            </span>
          )}
        </div>

        {/* Camera mode toggle - bottom right (only if both modes available) */}
        {hasSnapshot && hasStream && (
          <div className="absolute bottom-2 right-2 flex gap-1 bg-black/50 backdrop-blur-xs rounded-sm p-1">
            <Button
              type="button"
              variant={cameraMode === 'snapshot' ? 'primary' : 'subtle'}
              size="sm"
              onClick={() => setCameraMode('snapshot')}
              className="!p-1 !h-auto"
              title="Snapshot"
              aria-label="Snapshot mode"
              iconCenter={<ImageIcon className="w-4 h-4" />}
            />
            <Button
              type="button"
              variant={cameraMode === 'stream' ? 'primary' : 'subtle'}
              size="sm"
              onClick={() => setCameraMode('stream')}
              className="!p-1 !h-auto"
              title="Stream"
              aria-label="Stream mode"
              iconCenter={<VideoIcon className="w-4 h-4" />}
            />
          </div>
        )}

        {/* Camera indicator - top left */}
        {hasCameraUrls && !imageError && (
          <div className="absolute top-2 left-2">
            <CameraIcon className="w-5 h-5 text-white/70 drop-shadow-sm" />
          </div>
        )}
      </div>

      {/* Footer - printer name and info */}
      <div className="p-3">
        <div className="font-bold text-base text-pf-text-primary font-bebas uppercase truncate">
          {p.name}
        </div>
        {p.modelName && (
          <div className="text-pf-text-secondary text-xs truncate">
            {p.modelName}
          </div>
        )}
      </div>
    </div>
  );
}
