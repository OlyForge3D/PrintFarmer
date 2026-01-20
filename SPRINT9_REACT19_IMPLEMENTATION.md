# Sprint 9: React 19 Adoption Implementation Plan

**Status:** 🚀 READY TO START  
**Objective:** Migrate high-impact forms and components to React 19 patterns  
**Duration:** 8-10 hours  
**Start Date:** January 15, 2026  

---

## Current Status

✅ **Foundation Ready:**
- useReact19Patterns.ts updated with `useAsyncAction` helper
- React 19 Adoption Guide created (REACT19_ADOPTION_GUIDE.md)
- All tests passing: 400/400 ✅
- Build successful: 9.70s ✅
- ESLint: Clean

---

## Phase 1: High-Impact Forms (3-4 hours)

### 1.1 ModelUploadModal.tsx (1 hour)
**Current State:** Uses useState for form state, manual error handling  
**Target:** Migrate to useActionState pattern

**Changes:**
```typescript
// Before: Manual form state management
const [isSubmitting, setIsSubmitting] = useState(false);
const [error, setError] = useState<string | null>(null);

const handleSubmit = async (formData: FormData) => {
  setIsSubmitting(true);
  try {
    await fileService.uploadModel(formData);
    setError(null);
  } catch (err) {
    setError(err.message);
  } finally {
    setIsSubmitting(false);
  }
};

// After: useActionState pattern
interface UploadState {
  error?: string;
  success?: boolean;
}

async function uploadModelAction(prevState: UploadState, formData: FormData): Promise<UploadState> {
  try {
    await fileService.uploadModel(formData);
    return { success: true };
  } catch (error) {
    return { ...prevState, error: error.message };
  }
}

const [state, formAction, isPending] = useAsyncAction(uploadModelAction, {});
```

**Files to Modify:**
- [src/Web/ReactApp/src/features/models3d/components/ModelUploadModal.tsx](src/Web/ReactApp/src/features/models3d/components/ModelUploadModal.tsx)
- Tests: Update upload test cases to work with useActionState

**QA Checklist:**
- [ ] Upload form submits correctly
- [ ] Error messages display properly
- [ ] Loading state shows during upload
- [ ] Progress callbacks still work
- [ ] Tests pass

---

### 1.2 UserManagementPage.tsx - Create User Form (1 hour)
**Current State:** Complex form with multiple useState calls, manual validation  
**Target:** Migrate creation form to useActionState

**Key Changes:**
```typescript
interface CreateUserState {
  error?: string;
  success?: boolean;
  usernameStatus?: 'idle' | 'checking' | 'taken' | 'available';
  emailStatus?: 'idle' | 'checking' | 'taken' | 'available';
}

async function createUserAction(prevState: CreateUserState, formData: FormData): Promise<CreateUserState> {
  const username = formData.get('username') as string;
  const email = formData.get('email') as string;
  const password = formData.get('password') as string;

  // Validation
  if (!username.trim()) return { ...prevState, error: 'Username required' };
  if (!email.trim()) return { ...prevState, error: 'Email required' };
  if (password.length < 8) return { ...prevState, error: 'Password must be 8+ characters' };

  try {
    // Check availability
    const availability = await apiClient.checkUserAvailability(username, email);
    if (availability.usernameExists) return { ...prevState, error: 'Username already taken' };
    if (availability.emailExists) return { ...prevState, error: 'Email already in use' };

    // Create user
    await apiClient.createUser({ username, email, password });
    return { success: true };
  } catch (error) {
    return { ...prevState, error: 'Failed to create user' };
  }
}

const [state, formAction, isPending] = useAsyncAction(createUserAction, {});
```

**Files to Modify:**
- [src/Web/ReactApp/src/features/admin/pages/UserManagementPage.tsx](src/Web/ReactApp/src/features/admin/pages/UserManagementPage.tsx)

---

### 1.3 SetupWizard.tsx - Multi-step Form (1-2 hours)
**Current State:** Very complex, 900+ lines, multiple useState calls for each step  
**Target:** Refactor to cleaner useActionState-based multi-step pattern

**Strategy:**
- Keep existing step state for navigation
- Migrate account creation step to useActionState
- Migrate network config step to useActionState
- Keep remaining steps as-is (can be migrated later)

**Implementation:**
```typescript
interface SetupWizardState {
  step: number;
  error?: string;
  success?: boolean;
}

async function setupAccountAction(prevState: SetupWizardState, formData: FormData): Promise<SetupWizardState> {
  const username = formData.get('username') as string;
  const password = formData.get('password') as string;

  // Validation
  if (username.length < 3) return { ...prevState, error: 'Username too short' };
  if (password.length < 8) return { ...prevState, error: 'Password too short' };

  try {
    await apiClient.setupAdmin({ username, password });
    return { ...prevState, step: prevState.step + 1, success: true };
  } catch (error) {
    return { ...prevState, error: error.message };
  }
}

const [state, formAction, isPending] = useAsyncAction(setupAccountAction, { step: 0 });
```

**Files to Modify:**
- [src/Web/ReactApp/src/features/auth/components/SetupWizard.tsx](src/Web/ReactApp/src/features/auth/components/SetupWizard.tsx)

---

## Phase 2: Async Data Fetching (2-3 hours)

### 2.1 PrinterDetailsModal.tsx (1 hour)
**Current State:** useQuery + manual loading/error states  
**Target:** Migrate to use() hook with Suspense

**Pattern:**
```typescript
async function getPrinterPromise(printerId: string) {
  return apiClient.getPrinter(printerId);
}

function PrinterDetailsContent({ printerPromise }: { printerPromise: Promise<Printer> }) {
  const printer = use(printerPromise);
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

**Files to Modify:**
- [src/Web/ReactApp/src/features/printers/components/PrinterDetailsModal.tsx](src/Web/ReactApp/src/features/printers/components/PrinterDetailsModal.tsx)

---

### 2.2 JobDetailsModal.tsx (1 hour)
**Current State:** Manual promise handling with state  
**Target:** use() hook with Suspense

---

## Phase 3: Component API Cleanup (2-3 hours)

### 3.1 Scan and Identify forwardRef Usage
```bash
cd /home/pi/pfarm/src/Web/ReactApp
grep -r "forwardRef" src/ --include="*.tsx" --include="*.ts"
```

**Expected matches:** 5-10 components (to be identified)

### 3.2 Remove forwardRef Wrappers
For each component found:
1. Remove `forwardRef` wrapper
2. Add `ref` to props interface
3. Update usage sites
4. Run tests

**Example Pattern:**
```typescript
// Before
const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ children, ...props }, ref) => (
    <button ref={ref} {...props}>{children}</button>
  )
);

// After
function Button({ children, ref, ...props }: ButtonProps & { ref?: React.Ref<HTMLButtonElement> }) {
  return <button ref={ref} {...props}>{children}</button>;
}
```

---

## Implementation Checklist

### Setup (30 minutes)
- [ ] Review REACT19_ADOPTION_GUIDE.md
- [ ] Read useReact19Patterns.ts utilities
- [ ] Understand useAsyncAction hook
- [ ] Create feature branch: `feat/react19-adoption`

### Phase 1: Forms (3-4 hours)
- [ ] **1.1 ModelUploadModal**
  - [ ] Migrate to useAsyncAction
  - [ ] Update tests
  - [ ] Verify upload still works
  - [ ] Test error handling

- [ ] **1.2 UserManagementPage - Create Form**
  - [ ] Migrate to useAsyncAction
  - [ ] Keep availability checking
  - [ ] Update tests
  - [ ] Verify error messages

- [ ] **1.3 SetupWizard**
  - [ ] Identify each step's form logic
  - [ ] Migrate account step to useActionState
  - [ ] Migrate network step to useActionState
  - [ ] Update tests
  - [ ] Verify multi-step flow

### Phase 2: Async Data (2-3 hours)
- [ ] **2.1 PrinterDetailsModal**
  - [ ] Create getPrinterPromise function
  - [ ] Create Content component with use()
  - [ ] Wrap with Suspense/ErrorBoundary
  - [ ] Update tests
  - [ ] Verify details load

- [ ] **2.2 JobDetailsModal**
  - [ ] Create getJobPromise function
  - [ ] Create Content component with use()
  - [ ] Wrap with Suspense/ErrorBoundary
  - [ ] Update tests

- [ ] **2.3 FileDetailsModal** (Optional)
  - [ ] Migrate pattern
  - [ ] Update tests

### Phase 3: Cleanup (2-3 hours)
- [ ] Scan codebase for forwardRef usage
- [ ] Create list of components to update
- [ ] Remove forwardRef wrappers (highest priority first)
- [ ] Update component usage sites
- [ ] Run tests after each removal
- [ ] Update type definitions

### Quality Assurance (1-2 hours)
- [ ] Run full test suite: `npm run test:run`
- [ ] Run linting: `npm run lint`
- [ ] Build production: `npm run build`
- [ ] Manual smoke testing of forms
- [ ] Manual smoke testing of modals
- [ ] Check browser console for warnings

### Documentation (30 minutes)
- [ ] Update FRONTEND_UI_COMPONENTS.md with React 19 patterns
- [ ] Add "Migrated to React 19" notes to modified components
- [ ] Create MIGRATION_LOG.md documenting all changes
- [ ] Update sprint section in UI_ENHANCEMENT_ROADMAP.md

---

## Success Criteria

✅ **All Completion Criteria:**
- [ ] 3+ form components migrated to useActionState
- [ ] 2+ async data patterns migrated to use() + Suspense
- [ ] 5+ forwardRef usages removed
- [ ] All 400+ tests passing
- [ ] ESLint: 0 errors, 0 warnings
- [ ] Build succeeds in <11 seconds
- [ ] No console errors or warnings
- [ ] Component documentation updated

---

## Time Estimates

| Phase | Component | Est. Time | Actual |
|-------|-----------|-----------|--------|
| 1.1 | ModelUploadModal | 1h | |
| 1.2 | UserManagementPage | 1h | |
| 1.3 | SetupWizard | 1-2h | |
| 2.1 | PrinterDetailsModal | 1h | |
| 2.2 | JobDetailsModal | 1h | |
| 2.3 | FileDetailsModal | 0.5h | |
| 3 | forwardRef cleanup | 2-3h | |
| QA | Testing & validation | 1-2h | |
| Docs | Documentation | 0.5h | |
| **TOTAL** | | **8-10h** | |

---

## Notes

- SetupWizard is complex; consider refactoring in stages
- Some forms use custom validation; ensure error handling still works
- Test upload progress callbacks to ensure they aren't broken
- Consider breaking SetupWizard into smaller components for better maintainability

---

## Next Steps (Sprint 10+)

- Migrate remaining form components
- Consider Server Components for async data
- Implement Activity component for tab state
- Create React 19 patterns linter rules
- Documentation refresh

