#!/bin/bash

# Update all moved component imports - Batch 2

cd /home/pi/pfarm/src/Web/ReactApp/src

# Printer components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/CollapsedPrinterCard'|from '@/features/printers/components/CollapsedPrinterCard'|g" \
  -e "s|from '@/components/DetailedPrinterCard'|from '@/features/printers/components/DetailedPrinterCard'|g" \
  -e "s|from '@/components/EnhancedPrinterCard'|from '@/features/printers/components/EnhancedPrinterCard'|g" \
  -e "s|from '@/components/ExpandablePrinterCard'|from '@/features/printers/components/ExpandablePrinterCard'|g" \
  -e "s|from '@/components/PrinterActionsDropdown'|from '@/features/printers/components/PrinterActionsDropdown'|g" \
  -e "s|from '@/components/PrinterDashboard'|from '@/features/printers/components/PrinterDashboard'|g" \
  -e "s|from '@/components/PrinterDetailsSidebar'|from '@/features/printers/components/PrinterDetailsSidebar'|g" \
  -e "s|from '@/components/PrinterFilesModal'|from '@/features/printers/components/PrinterFilesModal'|g" \
  -e "s|from '@/components/PrinterHistoryModal'|from '@/features/printers/components/PrinterHistoryModal'|g" \
  -e "s|from '@/components/PrinterTableView'|from '@/features/printers/components/PrinterTableView'|g" \
  -e "s|from '@/components/SystemHealth'|from '@/features/printers/components/SystemHealth'|g" \
  -e "s|from '@/components/DebugPrinterSignalRPanel'|from '@/features/printers/components/DebugPrinterSignalRPanel'|g" \
  {} \;

# Catalog components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/FilamentTypeSelector'|from '@/features/catalog/components/FilamentTypeSelector'|g" \
  -e "s|from '@/components/ColorFamilySelect'|from '@/features/catalog/components/ColorFamilySelect'|g" \
  -e "s|from '@/components/ColorSwatch'|from '@/features/catalog/components/ColorSwatch'|g" \
  -e "s|from '@/components/LocationManagement'|from '@/features/catalog/components/LocationManagement'|g" \
  -e "s|from '@/components/LocationSelector'|from '@/features/catalog/components/LocationSelector'|g" \
  -e "s|from '@/components/SpoolmanFilamentImportButton'|from '@/features/catalog/components/SpoolmanFilamentImportButton'|g" \
  -e "s|from '@/components/SpoolUsageBar'|from '@/features/catalog/components/SpoolUsageBar'|g" \
  -e "s|from '@/components/TagEditor'|from '@/features/catalog/components/TagEditor'|g" \
  {} \;

# Model components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/EditModelModal'|from '@/features/models3d/components/EditModelModal'|g" \
  {} \;

# Slicer components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/ProfileSelector'|from '@/features/slicer/components/ProfileSelector'|g" \
  -e "s|from '@/components/SlicerConfirmModal'|from '@/features/slicer/components/SlicerConfirmModal'|g" \
  -e "s|from '@/components/WorkerSelector'|from '@/features/slicer/components/WorkerSelector'|g" \
  {} \;

# Common layout components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/Layout'|from '@/common/components/Layout'|g" \
  -e "s|from '@/components/PageHeader'|from '@/common/components/PageHeader'|g" \
  -e "s|from '@/components/PageTemplate'|from '@/common/components/PageTemplate'|g" \
  -e "s|from '@/components/ErrorBoundary'|from '@/common/components/ErrorBoundary'|g" \
  -e "s|from '@/components/BuildInfo'|from '@/common/components/BuildInfo'|g" \
  -e "s|from '@/components/PrintFarmerLogo'|from '@/common/components/PrintFarmerLogo'|g" \
  -e "s|from '@/components/Skeleton'|from '@/common/components/skeletons/Skeleton'|g" \
  -e "s|from '@/components/ThemeToggle'|from '@/common/components/ThemeToggle'|g" \
  -e "s|from '@/components/ViewModeToggle'|from '@/common/components/ViewModeToggle'|g" \
  -e "s|from '@/components/BackendSelector'|from '@/common/components/BackendSelector'|g" \
  {} \;

# Auth components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/SetupWizard'|from '@/features/auth/components/SetupWizard'|g" \
  -e "s|from '@/components/EmailConfirmationBanner'|from '@/features/auth/components/EmailConfirmationBanner'|g" \
  -e "s|from '@/components/AccessDenied'|from '@/features/auth/components/AccessDenied'|g" \
  {} \;

# Modal components
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e "s|from '@/components/ImportResultsModal'|from '@/common/components/modals/ImportResultsModal'|g" \
  {} \;

# Also handle double-quoted imports
find . -type f \( -name "*.tsx" -o -name "*.ts" \) -exec sed -i \
  -e 's|from "@/components/Layout"|from "@/common/components/Layout"|g' \
  -e 's|from "@/components/ErrorBoundary"|from "@/common/components/ErrorBoundary"|g' \
  -e 's|from "@/components/SetupWizard"|from "@/features/auth/components/SetupWizard"|g' \
  -e 's|from "@/components/PrinterDashboard"|from "@/features/printers/components/PrinterDashboard"|g' \
  -e 's|from "@/components/SystemHealth"|from "@/features/printers/components/SystemHealth"|g' \
  {} \;

echo "✓ Updated all component import paths"
