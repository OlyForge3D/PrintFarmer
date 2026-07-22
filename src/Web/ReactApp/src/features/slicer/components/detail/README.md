# Profile Detail Components

Rich readonly profile detail views with inheritance visualization.

## Components

### ProfileDetailView

Main component for displaying all profile settings organized by category with inheritance indicators.

```tsx
<ProfileDetailView
  profileType="process"
  profile={processProfile}
  parentProfile={parentProcessProfile}
  profileName="My Custom Profile"
  parentName="Base Profile"
/>
```

**Features:**
- Groups fields by category with collapsible sections
- Shows field counts per category (total + overridden count)
- Inheritance indicators: blue = inherited, orange = overridden, gray = standalone
- "Show inherited" toggle to filter to only overridden fields
- Expand/collapse all controls
- Displays parent values for overridden fields

### ProfileFieldRow

Individual readonly field display with inheritance badge.

```tsx
<ProfileFieldRow
  field={fieldMetadata}
  value={currentValue}
  parentValue={parentValue}
  hasParent={true}
  parentName="Base Profile"
/>
```

**Features:**
- Formatted value display (boolean → Yes/No, numbers with units, enum labels)
- Inheritance badge with tooltip
- Parent value shown in muted text for overridden fields
- Field description tooltip (ⓘ icon)

### InheritanceBadge

Small colored dot badge showing inheritance status.

```tsx
<InheritanceBadge
  status="overridden"
  parentName="Base Profile"
/>
```

**Status values:**
- `inherited` - Blue dot, "Inherited from {parentName}" tooltip
- `overridden` - Orange dot, "Overridden" tooltip
- `standalone` - Gray dot, no tooltip

### ProfileInheritanceTree

Visual tree showing the inheritance chain.

```tsx
<ProfileInheritanceTree
  profileName="My Custom Profile"
  parentChain={[
    { name: "Generic Printer", id: "generic" },
    { name: "Base Profile", id: "base" }
  ]}
/>
```

**Features:**
- Vertical tree with connecting lines
- Current profile highlighted with accent border
- Shows "Root ancestor" and "Current profile" labels
- Compact design for sidebar/header placement

## Integration Example

```tsx
import { ProfileDetailView, ProfileInheritanceTree } from '@/features/slicer/components/detail';

function ProfileDetailsPage({ profileId }: { profileId: string }) {
  const { data: profile } = useProcessProfile(profileId);
  const { data: parent } = useProcessProfile(profile?.inherits);

  return (
    <div className="grid grid-cols-[300px_1fr] gap-6">
      {/* Sidebar */}
      <div>
        <ProfileInheritanceTree
          profileName={profile.name}
          parentChain={buildParentChain(profile)}
        />
      </div>

      {/* Main content */}
      <div>
        <ProfileDetailView
          profileType="process"
          profile={profile.settings}
          parentProfile={parent?.settings}
          parentName={parent?.name}
        />
      </div>
    </div>
  );
}
```

## Data Requirements

All components require:
- Profile schema via `useProfileSchema(profileType)` hook
- Profile settings as `Record<string, unknown>`
- Parent profile settings (optional) as `Record<string, unknown>`

The schema provides field metadata including:
- `key` - setting key in the profile
- `label` - display name
- `fieldType` - 'number' | 'integer' | 'boolean' | 'string' | 'enum'
- `category` - grouping category
- `unit` - measurement unit (e.g., "mm", "mm/s")
- `options` - enum choices with labels
- `description` - help text

## Styling

Uses PrintFarmer design tokens:
- `bg-pf-bg-0` - card backgrounds
- `text-pf-text-primary` - primary text
- `text-pf-text-muted` - secondary text
- `border-pf-border` - borders
- `bg-pf-accent-bg` - hover states
- `border-pf-accent` - current profile accent
- Custom colors: `bg-blue-500`, `bg-orange-500`, `bg-gray-400` for badges
