/**
 * React 19 Patterns Guide and Utilities
 * 
 * This file documents and provides utilities for React 19 features:
 * - use() hook for promise/context consumption
 * - useActionState for form handling
 * - Ref as prop (no forwardRef needed)
 * - useEffectEvent for non-reactive logic
 */

import { useCallback, useTransition, useActionState } from 'react';

/**
 * Example: Using React 19 use() hook with Suspense
 * 
 * ```tsx
 * import { use, Suspense } from 'react';
 * 
 * async function fetchUser(id: string) {
 *   const res = await fetch(`/api/users/${id}`);
 *   return res.json();
 * }
 * 
 * function UserProfile({ userPromise }: { userPromise: Promise<User> }) {
 *   const user = use(userPromise);
 *   return <div>{user.name}</div>;
 * }
 * 
 * export function UserPage({ userId }: { userId: string }) {
 *   return (
 *     <Suspense fallback={<div>Loading...</div>}>
 *       <UserProfile userPromise={fetchUser(userId)} />
 *     </Suspense>
 *   );
 * }
 * ```
 */

/**
 * Example: Using React 19 useActionState for forms
 * 
 * ```tsx
 * import { useActionState } from 'react';
 * 
 * async function submitForm(prevState, formData: FormData) {
 *   const name = formData.get('name');
 *   
 *   try {
 *     const res = await fetch('/api/submit', {
 *       method: 'POST',
 *       body: JSON.stringify({ name })
 *     });
 *     
 *     if (!res.ok) {
 *       return { error: 'Failed to submit' };
 *     }
 *     
 *     return { success: true };
 *   } catch (error) {
 *     return { error: error.message };
 *   }
 * }
 * 
 * export function MyForm() {
 *   const [state, formAction, isPending] = useActionState(submitForm, {});
 *   
 *   return (
 *     <form action={formAction}>
 *       <input name="name" required />
 *       <button type="submit" disabled={isPending}>
 *         {isPending ? 'Submitting...' : 'Submit'}
 *       </button>
 *       {state.error && <p className="error">{state.error}</p>}
 *       {state.success && <p className="success">Submitted!</p>}
 *     </form>
 *   );
 * }
 * ```
 */

/**
 * Example: React 19 Ref as Prop (no forwardRef needed)
 * 
 * ```tsx
 * interface CustomInputProps {
 *   placeholder?: string;
 *   ref?: React.Ref<HTMLInputElement>;  // ref is now just a regular prop!
 * }
 * 
 * export function CustomInput({ placeholder, ref }: CustomInputProps) {
 *   return <input ref={ref} placeholder={placeholder} />;
 * }
 * 
 * // Usage - no forwardRef wrapper needed!
 * function ParentComponent() {
 *   const inputRef = useRef<HTMLInputElement>(null);
 *   
 *   return (
 *     <>
 *       <CustomInput ref={inputRef} placeholder="Type here" />
 *       <button onClick={() => inputRef.current?.focus()}>
 *         Focus
 *       </button>
 *     </>
 *   );
 * }
 * ```
 */

/**
 * Example: React 19 useEffectEvent (new hook)
 * 
 * Extracts non-reactive logic from effects. Useful when you want to access
 * latest values without adding them to dependency array.
 * 
 * ```tsx
 * import { useEffect, useEffectEvent } from 'react';
 * 
 * interface ChatProps {
 *   roomId: string;
 *   theme: 'light' | 'dark';
 * }
 * 
 * export function ChatRoom({ roomId, theme }: ChatProps) {
 *   const [messages, setMessages] = useState([]);
 * 
 *   // useEffectEvent lets us use theme without it being in dependencies
 *   const onMessage = useEffectEvent((message: string) => {
 *     console.log(`Message in ${theme} theme:`, message);
 *     setMessages(prev => [...prev, message]);
 *   });
 * 
 *   useEffect(() => {
 *     const connection = createConnection(roomId);
 *     connection.on('message', onMessage);
 *     connection.connect();
 * 
 *     return () => {
 *       connection.disconnect();
 *     };
 *   }, [roomId]); // theme NOT in dependencies!
 * 
 *   return <div>{messages.length} messages</div>;
 * }
 * ```
 */

/**
 * Example: Context without Provider (React 19)
 * 
 * ```tsx
 * import { createContext, useContext, useState } from 'react';
 * 
 * const ThemeContext = createContext<ThemeValue>('light');
 * 
 * export function App() {
 *   const [theme, setTheme] = useState('light');
 * 
 *   // React 19: Render context directly instead of Context.Provider!
 *   return (
 *     <ThemeContext value={theme}>
 *       <Header />
 *       <Main />
 *       <Footer />
 *     </ThemeContext>
 *   );
 * }
 * ```
 */

/**
 * Hook demonstrating React 19 useActionState pattern
 * 
 * Usage:
 * ```tsx
 * const [state, formAction, isPending] = useActionStatePattern(submitFn, initialState);
 * ```
 */
export function useActionStatePattern<T extends Record<string, unknown>>(
  action: (prevState: T) => Promise<T>,
  initialState: T
): [T, (_: FormData) => void, boolean] {
  const [isPending, startTransition] = useTransition();
  const [state] = useActionState(action, initialState);

  const formAction = useCallback(
    () => {
      startTransition(() => {
        // In real React 19, formAction would be passed directly to form
        // This is a simplified pattern
      });
    },
    []
  );

  return [state, formAction, isPending];
}

/**
 * Advanced React 19 Pattern: useAsyncAction
 * 
 * Combines useActionState with error handling and loading states
 * Perfect for multi-step forms and async operations
 * 
 * @example
 * ```tsx
 * async function createUser(prevState: CreateUserState, formData: FormData) {
 *   const username = formData.get('username') as string;
 *   
 *   // Validation
 *   if (!username.trim()) {
 *     return { ...prevState, error: 'Username required' };
 *   }
 *   
 *   try {
 *     const user = await apiClient.createUser({ username });
 *     return { success: true, data: user };
 *   } catch (error) {
 *     return { 
 *       ...prevState, 
 *       error: error instanceof Error ? error.message : 'Failed to create user'
 *     };
 *   }
 * }
 * 
 * function CreateUserForm() {
 *   const [state, formAction, isPending] = useAsyncAction(createUser, { error: null });
 *   
 *   return (
 *     <form action={formAction}>
 *       <input name="username" required />
 *       {state.error && <p className="text-red-600">{state.error}</p>}
 *       <button disabled={isPending}>{isPending ? 'Creating...' : 'Create'}</button>
 *     </form>
 *   );
 * }
 * ```
 */
export function useAsyncAction<T extends Record<string, unknown>>(
  action: (prevState: T, formData: FormData) => Promise<T>,
  initialState: T
): [T, (formData: FormData) => void, boolean] {
  const [isPending, startTransition] = useTransition();
  const [state, formAction] = useActionState(action, initialState);

  const boundFormAction = useCallback(
    (formData: FormData) => {
      startTransition(() => {
        formAction(formData);
      });
    },
    [formAction]
  );

  return [state, boundFormAction, isPending];
}

/**
 * Documentation: When to use React 19 features
 * 
 * ✅ use() hook:
 *   - When fetching data inside components
 *   - For reading context values
 *   - Works great with Suspense boundaries
 *   - Better than direct await for clarity
 * 
 * ✅ useActionState:
 *   - Form submissions (better than manual state + handlers)
 *   - Keeps form state and action logic together
 *   - Automatic pending state tracking
 *   - Built-in error handling
 * 
 * ✅ Ref as prop:
 *   - Simplifies component APIs
 *   - No need for forwardRef wrappers
 *   - Cleaner type definitions
 * 
 * ✅ useEffectEvent:
 *   - Extract non-reactive logic from effects
 *   - Use latest values without dependencies
 *   - Cleaner dependency arrays
 */

export const React19PatternsGuide = {
  description: 'React 19 patterns and utilities guide',
  patterns: [
    'use() for promise/context consumption',
    'useActionState for form handling',
    'Ref as prop (no forwardRef needed)',
    'useEffectEvent for non-reactive logic',
    'Context without Provider wrapper',
    'useFormStatus for form input state'
  ]
};
