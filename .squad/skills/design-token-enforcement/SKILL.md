# Skill: Design Token Enforcement

## Purpose
Ensure all UI code uses the PrintFarmer design system tokens (`pf-*`) instead of raw Tailwind utility classes for colors, ensuring theme consistency across all three supported themes.

## When to Use
- When creating new UI components or pages
- When reviewing code for design system compliance
- When migrating legacy code to use design tokens

## Token Mappings

### Text Colors
| Raw Tailwind | Replace With |
|--------------|--------------|
| `text-gray-100`, `text-gray-200` | `text-pf-text-light` |
| `text-gray-300`, `text-gray-400` | `text-pf-text-secondary` |
| `text-gray-500`, `text-gray-600` | `text-pf-text-muted` or `text-pf-text-tertiary` |
| `text-gray-700`, `text-gray-800`, `text-gray-900` | `text-pf-text-primary` |
| `text-white` | `text-white` (OK for contrast on colored backgrounds) |
| `text-blue-*` | `text-pf-accent` or `text-pf-link` |
| `text-green-*` | `text-pf-success` |
| `text-red-*` | `text-pf-error` or `text-pf-error-text` |
| `text-yellow-*`, `text-orange-*` | `text-pf-warning` or `text-pf-warning-text` |

### Background Colors
| Raw Tailwind | Replace With |
|--------------|--------------|
| `bg-gray-900`, `bg-gray-950` | `bg-pf-bg-0` |
| `bg-gray-800` | `bg-pf-bg-1` |
| `bg-gray-700` | `bg-pf-bg-2` |
| `bg-blue-*` | `bg-pf-accent-bg` or `bg-pf-accent` |
| `bg-green-*` | `bg-pf-success-bg` or `bg-pf-success` |
| `bg-red-*` | `bg-pf-error-bg` or `bg-pf-error` |

### Border Colors
| Raw Tailwind | Replace With |
|--------------|--------------|
| `border-gray-200`, `border-gray-300` | `border-pf-border-light` |
| `border-gray-400`, `border-gray-500` | `border-pf-border` |
| `border-gray-600`, `border-gray-700` | `border-pf-border-medium` or `border-pf-border-dark` |
| `border-blue-*` | `border-pf-accent` |
| `border-green-*` | `border-pf-success` |
| `border-red-*` | `border-pf-error-border` |

### Focus Rings
| Raw Tailwind | Replace With |
|--------------|--------------|
| `focus:ring-blue-*` | `focus:ring-pf-accent` or `focus-visible:ring-pf-accent` |
| `focus:ring-green-*` | `focus:ring-pf-accent` (use accent for consistency) |
| `ring-blue-*` | `ring-pf-accent` |

## Commands

### Find Violations
```bash
# Find raw gray color usage
grep -rn --include="*.tsx" --include="*.ts" "text-gray\|bg-gray\|border-gray" src/Web/ReactApp/src/

# Find raw focus ring colors
grep -rn --include="*.tsx" "focus:ring-blue\|focus:ring-green\|focus:ring-red" src/Web/ReactApp/src/
```

### Automated Replacement (Use with caution)
```bash
# Example: Replace text-gray-400 with text-pf-text-secondary
sed -i '' 's/text-gray-400/text-pf-text-secondary/g' path/to/file.tsx
```

## Verification
After making changes:
1. Switch between all three themes (GitHub Dark, PrintFarmer Dark, Light)
2. Verify colors update correctly on theme change
3. Check contrast ratios remain accessible (4.5:1 for text, 3:1 for large text)

## Theme Files Reference
- `src/Web/ReactApp/src/styles/theme.css` — Main theme entry point
- `src/Web/ReactApp/src/styles/themes/github-dark.css`
- `src/Web/ReactApp/src/styles/themes/printfarmer-dark.css`
- `src/Web/ReactApp/src/styles/themes/light.css`
- `src/Web/ReactApp/tailwind.config.js` — Token class definitions
