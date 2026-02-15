# Modal Component Template

A reusable, consistent Modal component for all modal dialogs across the application.

## Location
`src/common/components/modals/Modal.tsx`

## Features

- **Consistent Styling**: All modals have the same look and feel
- **Backdrop with Blur**: Fixed position backdrop with blur effect
- **Sticky Header**: Title and close button remain visible when scrolling
- **Scrollable Content**: Content area scrolls independently
- **Optional Footer**: For action buttons (Cancel, Save, etc.)
- **Keyboard Navigation**: Escape key closes modal
- **Click-Outside-to-Close**: Click on backdrop closes modal (unless disabled)
- **Accessibility**: Proper ARIA labels and button semantics

## Basic Usage

```tsx
import { Modal } from '@/common/components/modals';
import { useState } from 'react';
import { Button } from '@/common/components/ui/Button';

export function MyComponent() {
  const [isOpen, setIsOpen] = useState(false);

  return (
    <>
      <Button onClick={() => setIsOpen(true)}>Open Modal</Button>

      <Modal
        isOpen={isOpen}
        onClose={() => setIsOpen(false)}
        title="My Modal Title"
        footer={
          <>
            <Button variant="secondary" onClick={() => setIsOpen(false)}>
              Cancel
            </Button>
            <Button variant="primary" onClick={handleSave}>
              Save
            </Button>
          </>
        }
      >
        <p>Modal content goes here</p>
      </Modal>
    </>
  );
}
```

## Props

### Required Props
- `isOpen: boolean` - Whether the modal is open
- `onClose: () => void` - Callback when modal should close
- `title: string` - Modal title displayed in header
- `children: React.ReactNode` - Modal content

### Optional Props
- `footer?: React.ReactNode` - Footer content (action buttons, etc.)
- `width?: string` - Custom width class (default: `max-w-2xl`)
- `maxHeight?: string` - Custom max height class (default: `max-h-[90vh]`)
- `isDisabled?: boolean` - Disable interactions (e.g., during loading)
- `titleIcon?: React.ReactNode` - Icon to display next to title
- `closeAriaLabel?: string` - Aria label for close button

## Examples

### Modal with Icon
```tsx
<Modal
  isOpen={isOpen}
  onClose={onClose}
  title="Delete Item"
  titleIcon={<AlertIcon className="w-5 h-5 text-pf-error-text" />}
  footer={
    <>
      <Button variant="secondary" onClick={onClose}>
        Cancel
      </Button>
      <Button variant="primary" onClick={handleDelete}>
        Delete
      </Button>
    </>
  }
>
  <p>Are you sure you want to delete this item?</p>
</Modal>
```

### Modal with Disabled State
```tsx
<Modal
  isOpen={isOpen}
  onClose={onClose}
  title="Loading Data"
  isDisabled={isLoading}
  footer={
    <Button disabled={isLoading}>
      {isLoading ? 'Loading...' : 'Done'}
    </Button>
  }
>
  {isLoading ? <Spinner /> : <p>Data loaded successfully</p>}
</Modal>
```

### Modal with Custom Width
```tsx
<Modal
  isOpen={isOpen}
  onClose={onClose}
  title="Wide Modal"
  width="max-w-4xl"
>
  <p>This modal is wider than the default</p>
</Modal>
```

## Migration from Inline Modals

Existing inline modal implementations can be simplified by using the `Modal` component:

**Before:**
```tsx
{isOpen && (
  <div className="fixed inset-0 bg-black/50 backdrop-blur-sm flex items-center justify-center z-50">
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl shadow-xl max-w-2xl w-full mx-4">
      <div className="flex items-center justify-between p-6 border-b border-pf-border">
        <h3>My Modal</h3>
        <CloseIcon onClick={onClose} />
      </div>
      <div className="p-6">
        <p>Content here</p>
      </div>
    </div>
  </div>
)}
```

**After:**
```tsx
<Modal
  isOpen={isOpen}
  onClose={onClose}
  title="My Modal"
>
  <p>Content here</p>
</Modal>
```

## Benefits

1. **Consistency**: All modals look and behave the same way
2. **Accessibility**: Built-in keyboard navigation and ARIA labels
3. **Less Code**: No need to duplicate modal boilerplate
4. **Maintainability**: Changes to modal styling only need to be made in one place
5. **Better UX**: Consistent interactions across the application

## Import Location

Can be imported via direct path or barrel export:

```tsx
// Direct import
import { Modal } from '@/common/components/modals/Modal';

// Via barrel export (recommended)
import { Modal } from '@/common/components/modals';
```
