// Authentication API — plain exported functions sharing the axios instance
// from `httpClient.ts`. Split out of the `ApiClient` monolith (`services/api.ts`)
// so App.tsx/AuthContext no longer eagerly pull in the whole 486-method class.
// See issue #2343.
import { client } from "@/services/api/httpClient";
import type { AuthenticationResult, LoginRequest, RegisterRequest, UserDto } from "@/types/api";

export async function login(credentials: LoginRequest): Promise<AuthenticationResult> {
  // Backend expects the field name `UsernameOrEmail` (model uses UsernameOrEmail).
  // Frontend `LoginRequest` type historically used `username` so map that to
  // `usernameOrEmail` to remain backwards-compatible and avoid model binding
  // validation errors (400 Bad Request).
  const usernameOrEmail =
    (credentials as LoginRequest & { username?: string }).usernameOrEmail ??
    (credentials as LoginRequest & { username?: string }).username;

  const payload = {
    usernameOrEmail,
    password: credentials.password,
  } as Record<string, string>;

  const response = await client.post<AuthenticationResult>(
    "/auth/login",
    payload,
    { skipAuthRedirect: true },
  );
  return response.data;
}

export async function register(userData: RegisterRequest): Promise<AuthenticationResult> {
  const response = await client.post<AuthenticationResult>("/auth/register", userData);
  return response.data;
}

export async function getCurrentUser(): Promise<UserDto> {
  const response = await client.get<UserDto>("/auth/me");
  return response.data;
}

export async function logout(): Promise<void> {
  await client.post("/auth/logout");
}

export async function forgotPassword(
  email: string
): Promise<{ success: boolean; message: string }> {
  const response = await client.post<{
    success: boolean;
    message: string;
  }>("/auth/forgot-password", { email });
  return response.data;
}

export async function resetPassword(
  token: string,
  email: string,
  newPassword: string,
  confirmPassword: string
): Promise<{ success: boolean; message: string }> {
  const response = await client.post<{
    success: boolean;
    message: string;
  }>("/auth/reset-password", { token, email, newPassword, confirmPassword });
  return response.data;
}

export async function confirmEmail(
  token: string
): Promise<{ success: boolean; message: string }> {
  const response = await client.post<{
    success: boolean;
    message: string;
  }>("/auth/confirm-email", { token });
  return response.data;
}

export async function resendEmailConfirmation(): Promise<{
  success: boolean;
  message: string;
}> {
  const response = await client.post<{
    success: boolean;
    message: string;
  }>("/auth/resend-confirmation");
  return response.data;
}
