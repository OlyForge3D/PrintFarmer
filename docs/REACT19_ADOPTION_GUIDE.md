# React 19 Adoption Guide

A comprehensive guide to migrating PrintFarmer components to modern React 19 patterns for improved code quality, maintainability, and developer experience.

---

## Overview

React 19 introduces several powerful patterns that significantly improve form handling, async data fetching, and component APIs. This guide details practical migration strategies for PrintFarmer's codebase.

**Key Benefits:**
- 🎯 **Simplified Forms:** `useActionState` replaces manual form state management
- ⚡ **Cleaner Async:** `use()` hook eliminates prop drilling and wrapper components
- 🔧 **Better APIs:** Ref as prop removes forwardRef boilerplate
- 📦 **Smaller Bundle:** Suspense boundaries improve code splitting

---

## Pattern 1: useActionState for Forms

### What It Is
`useActionState` binds form submission to an async action function with automatic state tracking.

### Before (React 18)
```typescript
function CreateUserForm() {
  const [formData, setFormData] = useState({ username: '', email: '' });
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setIsSubmitting(true);
    try {
      await apiClient.createUser(formData);
      setFormData({ username: '', email: '' });
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      <input 
        value={formData.username}
        onChange={(e) => setFormData(p => ({ ...p, username: e.target.value }))}
      />
      {error && <p className="text-red-600">{error}</p>}
      <button disabled={isSubmitting}>{isSubmitting ? 'Creating...' : 'Create'}</button>
    </form>
  );
}
```

### After (React 19)
```typescript
interface CreateUserState {
  error?: string;
  success?: boolean;
}

async function createUserAction(prevState: CreateUserState, formData: FormData): Promise<CreateUserState> {
  const username = formData.get('username') as string;
  const email = formData.get('email') as string;

  // Validation
  if (!username.trim() || !email.trim()) {
    return { ...prevState, error: 'All fields required' };
  }

  try {
    await apiClient.createUser({ username, email });
    return { error: undefined, success: true };
  } catch (error) {
    return { 
      ...prevState,
      error: error instanceof Error ? error.message : 'Failed to create user'
    };
  }
}

function CreateUserForm() {
  const [state, formAction, isPending] = useAsyncAction(createUserAction, {});

  return (
    <form action={formAction}>
      <input name="username" placeholder="Username" required />
      <input name="email" type="email" placeholder="Email" required />
      {state.error && <p className="text-red-600">{state.error}</p>}
      {state.success && <p className="text-green-600">User created successfully!</p>}
      <button type="submit" disabled={isPending}>
        {isPending ? 'Creating...' : 'Create User'}
      </button>
    </form>
  );
}
```

### Benefits
- ✅ **Single source of truth:** Form state and action logic together
- ✅ **Automatic pending state:** No need to manually track `isSubmitting`
- ✅ **FormData API:** Works naturally with form elements
- ✅ **Type-safe:** TypeScript infers state types automatically

### Common Patterns

**Multi-step Forms:**
```typescript
interface SetupFormState {
  step: number;
  accountCreated: boolean;
  networkConfigured: boolean;
  error?: string;
}

async function setupAction(prevState: SetupFormState, formData: FormData): Promise<SetupFormState> {
  try {
    if (prevState.step === 0) {
      // Account creation
      await apiClient.createAdmin({
        username: formData.get('username') as string,
        password: formData.get('password') as string,
      });
      return { ...prevState, step: 1, accountCreated: true };
    } else if (prevState.step === 1) {
      // Network config
      await apiClient.configureNetwork({
        subnets: formData.getAll('subnets') as string[],
      });
      return { ...prevState, step: 2, networkConfigured: true };
    }
    return prevState;
  } catch (error) {
    return { ...prevState, error: 'Configuration failed' };
  }
}

function SetupWizard() {
  const [state, formAction, isPending] = useAsyncAction(setupAction, { 
    step: 0, 
    accountCreated: false, 
    networkConfigured: false 
  });

  return (
    <form action={formAction}>
      {state.step === 0 && (
        <>
          <input name="username" required />
          <input name="password" type="password" required />
        </>
      )}
      {state.step === 1 && (
        <input name="subnets" placeholder="10.0.0.0/24" />
      )}
      <button disabled={isPending}>
        {state.step === 0 ? 'Create Account' : 'Configure Network'}
      </button>
    </form>
  );
}
```

---

## Pattern 2: use() Hook for Async Data

### What It Is
`use()` suspends rendering until a promise resolves, simplifying async data patterns.

### Before (React 18)
```typescript
function PrinterDetails({ printerId }: { printerId: string }) {
  const [printer, setPrinter] = useState<Printer | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    let mounted = true;
    (async () => {
      try {
        const data = await apiClient.getPrinter(printerId);
        if (mounted) setPrinter(data);
      } catch (err) {
        if (mounted) setError(err as Error);
      } finally {
        if (mounted) setLoading(false);
      }
    })();

    return () => { mounted = false; };
  }, [printerId]);

  if (loading) return <LoadingSpinner />;
  if (error) return <ErrorMessage error={error} />;
  if (!printer) return null;

  return <div>{printer.name}</div>;
}

// Usage: Manual loading state in parent
function PrinterModal({ printerId }: { printerId: string }) {
  return <PrinterDetails printerId={printerId} />;
}
```

### After (React 19)
```typescript
async function getPrinterPromise(printerId: string) {
  return apiClient.getPrinter(printerId);
}

function PrinterDetailsContent({ printerPromise }: { printerPromise: Promise<Printer> }) {
  const printer = use(printerPromise); // Suspends until resolved
  return <div>{printer.name}</div>;
}

function PrinterDetailsModal({ printerId }: { printerId: string }) {
  return (
    <Dialog>
      <Suspense fallback={<LoadingSpinner />}>
        <ErrorBoundary fallback={<ErrorMessage />}>
          <PrinterDetailsContent printerPromise={getPrinterPromise(printerId)} />
        </ErrorBoundary>
      </Suspense>
    </Dialog>
  );
}
```

### Benefits
- ✅ **No manual state:** Suspense handles loading
- ✅ **Error boundaries:** Natural error handling
- ✅ **Cleaner components:** Focus on rendering, not state
- ✅ **Better composition:** Async data is explicit in props

### Common Patterns

**With useQuery (React Query):**
```typescript
function FileDetailsContent({ detailsPromise }: { detailsPromise: Promise<GcodeFile> }) {
  const file = use(detailsPromise);
  
  return (
    <div>
      <h2>{file.name}</h2>
      <p>{file.description}</p>
      <p>Size: {file.sizeBytes} bytes</p>
    </div>
  );
}

function FileDetailsModal({ fileId }: { fileId: string }) {
  // Create promise from queryFn
  const detailsPromise = apiClient.getGcodeFile(fileId);

  return (
    <Suspense fallback={<LoadingSpinner />}>
      <ErrorBoundary fallback={<ErrorMessage />}>
        <FileDetailsContent detailsPromise={detailsPromise} />
      </ErrorBoundary>
    </Suspense>
  );
}
```

---

## Pattern 3: Ref as Prop (No forwardRef)

### What It Is
React 19 allows components to accept `ref` directly as a prop, eliminating forwardRef wrapper.

### Before (React 18)
```typescript
interface CustomInputProps {
  placeholder?: string;
}

const CustomInput = forwardRef<HTMLInputElement, CustomInputProps>(
  function CustomInput({ placeholder }, ref) {
    return <input ref={ref} placeholder={placeholder} className="custom-input" />;
  }
);

CustomInput.displayName = 'CustomInput';
```

### After (React 19)
```typescript
interface CustomInputProps {
  placeholder?: string;
  ref?: React.Ref<HTMLInputElement>;
}

function CustomInput({ placeholder, ref }: CustomInputProps) {
  return <input ref={ref} placeholder={placeholder} className="custom-input" />;
}
```

### Benefits
- ✅ **Simpler API:** No forwardRef wrapper needed
- ✅ **Better types:** Props are self-documenting
- ✅ **Less boilerplate:** No displayName needed

---

## Pattern 4: useFormStatus for Form Feedback

### What It Is
`useFormStatus` provides the pending state of a form submission.

### Example
```typescript
function SubmitButton() {
  const { pending } = useFormStatus();

  return (
    <button 
      type="submit" 
      disabled={pending}
      className={pending ? 'opacity-50' : ''}
    >
      {pending ? 'Uploading...' : 'Upload File'}
    </button>
  );
}

function FileUploadForm() {
  const [state, formAction] = useActionState(uploadFileAction, {});

  return (
    <form action={formAction}>
      <input type="file" name="file" accept=".gcode" required />
      <input type="text" name="description" placeholder="File description" />
      <SubmitButton />
      {state.error && <p className="text-red-600">{state.error}</p>}
    </form>
  );
}
```

---

## Migration Priority

### Phase 1: High-Impact Forms (3-4 hours)
1. **SetupWizard.tsx** - Complex multi-step form
2. **ModelUploadModal.tsx** - File upload with progress
3. **UserManagementPage.tsx** - User creation with validation

### Phase 2: Async Data (2-3 hours)
1. **PrinterDetailsModal.tsx** - useQuery + use() pattern
2. **JobDetailsModal.tsx** - Job details fetching
3. **FileDetailsModal.tsx** - File details with Suspense

### Phase 3: API Cleanup (2-3 hours)
1. **Remove forwardRef** - Scan and replace all usages
2. **Simplify component definitions** - Remove wrapper components
3. **Document new patterns** - Update FRONTEND_UI_COMPONENTS.md

---

## Testing Strategies

### Testing useActionState
```typescript
describe('useActionState forms', () => {
  it('should submit form and update state', async () => {
    const mockAction = vi.fn(async (prev, formData) => ({
      ...prev,
      success: true,
      username: formData.get('username')
    }));

    const { result } = renderHook(() => 
      useAsyncAction(mockAction, { success: false })
    );

    const [, formAction] = result.current;
    const formData = new FormData();
    formData.set('username', 'testuser');

    act(() => {
      formAction(formData);
    });

    await waitFor(() => {
      expect(result.current[0].success).toBe(true);
    });
  });
});
```

### Testing use() with Suspense
```typescript
describe('use() hook with Suspense', () => {
  it('should suspend until promise resolves', async () => {
    const TestComponent = ({ promise }: { promise: Promise<string> }) => {
      const value = use(promise);
      return <div>{value}</div>;
    };

    const promise = Promise.resolve('Hello, React 19!');

    const { container } = render(
      <Suspense fallback={<div>Loading...</div>}>
        <TestComponent promise={promise} />
      </Suspense>
    );

    expect(container.textContent).toBe('Loading...');

    await waitFor(() => {
      expect(container.textContent).toBe('Hello, React 19!');
    });
  });
});
```

---

## Common Migration Checklist

- [ ] Identify all forms in component that use `useState` for form state
- [ ] Create async action function with proper TypeScript types
- [ ] Replace `useState` with `useAsyncAction` hook
- [ ] Replace form submission handlers with `action` prop
- [ ] Replace manual error/pending states with action state
- [ ] Test form submission flow
- [ ] Remove old handlers and update tests

---

## Resources

- [React 19 Docs: useActionState](https://react.dev/reference/react/useActionState)
- [React 19 Docs: use() Hook](https://react.dev/reference/react/use)
- [React 19 Docs: Form Actions](https://react.dev/learn/sync-external-store-with-react#supporting-multiple-event-sources)
- [Suspense for Data Fetching](https://react.dev/reference/react/Suspense)

