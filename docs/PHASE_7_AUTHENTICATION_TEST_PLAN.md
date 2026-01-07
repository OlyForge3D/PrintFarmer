# Phase 7: Authentication & Authorization - Test Plan

**Date:** 2025-01-09  
**Status:** Ready for Testing  
**Phase:** 7 - Hardening & Operational Polish

## Test Environment Setup

### Prerequisites
1. .NET 9 SDK installed
2. Node.js 18+ installed
3. Clean database (delete `farm.db` for fresh start)

### Start Services

**Terminal 1 - API Server:**
```bash
cd /Users/jpapiez/s/PFarm1/src
dotnet run --project ./api/Farm.Web.Api.csproj
# Wait for: "Now listening on: http://localhost:5245"
```

**Terminal 2 - React Dev Server:**
```bash
cd /Users/jpapiez/s/PFarm1/src/Web/ReactApp
npm run dev
# Wait for: "Local: http://localhost:3000/"
```

---

## Manual Test Scenarios

### 1. First-Run Setup (Admin Account Creation)

**Objective:** Verify initial admin account creation through SetupWizard

**Steps:**
1. Delete existing database: `rm /Users/jpapiez/s/PFarm1/src/farm.db`
2. Start API server (Terminal 1)
3. Navigate to http://localhost:3000/
4. Should see "WELCOME TO PRINTFARMER" setup wizard
5. Fill in admin account details:
   - Username: `admin`
   - Email: `admin@printfarmer.local`
   - Password: `Admin123!` (must meet password policy)
   - Confirm password: `Admin123!`
6. Click "Complete Setup"
7. Should redirect to printer dashboard
8. Should be automatically logged in as admin

**Expected Results:**
- ✅ Setup wizard appears on first run
- ✅ Password policy enforced (min length, complexity)
- ✅ Admin account created with `farm_admin` role
- ✅ User automatically logged in after setup
- ✅ JWT token stored in localStorage ('auth-token')
- ✅ API returns user with `roles: ["farm_admin"]`

**Verification:**
```bash
# Check database has admin user
sqlite3 farm.db "SELECT Username, Email FROM Users WHERE Id = (SELECT UserId FROM UserRoles WHERE RoleId = (SELECT Id FROM Roles WHERE Name = 'farm_admin'));"
# Should show: admin|admin@printfarmer.local
```

---

### 2. User Registration (Regular User)

**Objective:** Verify user registration creates non-admin account

**Steps:**
1. Navigate to http://localhost:3000/login
2. Click "Need an account? Register"
3. Fill in registration form:
   - Username: `testuser`
   - Email: `test@printfarmer.local`
   - Password: `Test123!`
   - Confirm password: `Test123!`
   - First Name: `Test` (optional)
   - Last Name: `User` (optional)
4. Click "Create Account"
5. Check response

**Expected Results:**
- ✅ Registration successful
- ✅ User created with `farm_user` role (default)
- ✅ User is active (IsActive = true)
- ✅ JWT token returned and stored
- ✅ Redirect to dashboard
- ✅ User can access public routes

**Verification:**
```bash
# Check user exists with farm_user role
sqlite3 farm.db "SELECT u.Username, r.Name FROM Users u JOIN UserRoles ur ON u.Id = ur.UserId JOIN Roles r ON ur.RoleId = r.Id WHERE u.Username = 'testuser';"
# Should show: testuser|farm_user
```

---

### 3. Login - Valid Credentials

**Objective:** Verify login with correct credentials

**Steps:**
1. Navigate to http://localhost:3000/login
2. Enter username: `admin`
3. Enter password: `Admin123!`
4. Click "Sign In"

**Expected Results:**
- ✅ Login successful
- ✅ JWT token stored in localStorage
- ✅ User object populated in AuthContext
- ✅ Redirect to dashboard
- ✅ isAuthenticated = true

**Verification:**
- Open browser DevTools → Application → Local Storage
- Check for `auth-token` key with JWT value
- Decode JWT at https://jwt.io/ - should show user info and roles

---

### 4. Login - Invalid Credentials

**Objective:** Verify login fails with wrong password

**Steps:**
1. Navigate to http://localhost:3000/login
2. Enter username: `admin`
3. Enter password: `WrongPassword123!`
4. Click "Sign In"

**Expected Results:**
- ✅ Login fails
- ✅ Error message displayed: "Login failed" or similar
- ✅ No token stored in localStorage
- ✅ User remains on login page
- ✅ isAuthenticated = false

---

### 5. Protected Routes - Unauthenticated Access

**Objective:** Verify unauthenticated users cannot access protected routes

**Steps:**
1. Clear localStorage: Open DevTools → Console → `localStorage.clear()`
2. Navigate to http://localhost:3000/dashboard
3. Check for redirect

**Expected Results:**
- ✅ Redirect to /login
- ✅ Original path preserved in location state
- ✅ "Authentication Required" message may appear

---

### 6. Protected Routes - Authenticated Non-Admin Access

**Objective:** Verify regular users cannot access admin routes

**Steps:**
1. Login as `testuser` (farm_user role)
2. Navigate to http://localhost:3000/admin/users
3. Check for access denial

**Expected Results:**
- ✅ "Access Denied" message displayed
- ✅ "You don't have permission to access this page" text shown
- ✅ User NOT shown admin page content

**Repeat for all admin routes:**
- /admin/observability
- /admin/printers  (alias to /printers; admin controls are permission-gated on that page)
- /admin/slicers
- /admin/logs
- /admin/slicer
- /admin/workers
- /slicer-profiles

---

### 7. Protected Routes - Admin Access

**Objective:** Verify admin users can access admin routes

**Steps:**
1. Login as `admin` (farm_admin role)
2. Navigate to http://localhost:3000/admin/users
3. Verify access granted

**Expected Results:**
- ✅ Admin page loads successfully
- ✅ No "Access Denied" message
- ✅ Page content displayed

**Repeat for all admin routes:**
- /admin/observability ✅
- /admin/printers ✅
- /admin/slicers ✅
- /admin/logs ✅
- /admin/slicer ✅
- /admin/workers ✅
- /slicer-profiles ✅

---

### 8. API Authorization - Admin Endpoints (Profiles)

**Objective:** Verify API enforces authorization on admin endpoints

**Test 8a: Unauthenticated Request**
```bash
# Try to import slicer profile without token
curl -X POST http://localhost:5245/api/slicer/profiles/import \
  -H "Content-Type: application/json" \
  -d '{"rawJson":"{}", "slicerType":"PrusaSlicer", "name":"Test"}'
```

**Expected:** `401 Unauthorized`

**Test 8b: Authenticated Non-Admin Request**
```bash
# Login as testuser, get token
TOKEN=$(curl -s -X POST http://localhost:5245/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"testuser","password":"Test123!"}' | jq -r '.token')

# Try to import profile with farm_user token
curl -X POST http://localhost:5245/api/slicer/profiles/import \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"rawJson":"{}", "slicerType":"PrusaSlicer", "name":"Test"}'
```

**Expected:** `403 Forbidden` (user lacks farm_admin role)

**Test 8c: Authenticated Admin Request**
```bash
# Login as admin, get token
ADMIN_TOKEN=$(curl -s -X POST http://localhost:5245/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Admin123!"}' | jq -r '.token')

# Try to import profile with farm_admin token
curl -X POST http://localhost:5245/api/slicer/profiles/import \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"rawJson":"{\"version\":\"1.0\"}", "slicerType":"PrusaSlicer", "name":"Test Profile"}'
```

**Expected:** `201 Created` or `200 OK` (admin has access)

---

### 9. API Authorization - Worker Management

**Test: Disable Worker**
```bash
# Get admin token
ADMIN_TOKEN=$(curl -s -X POST http://localhost:5245/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Admin123!"}' | jq -r '.token')

# List workers
curl -X GET http://localhost:5245/api/workers \
  -H "Authorization: Bearer $ADMIN_TOKEN"

# Try to disable worker (requires farm_admin)
# Replace WORKER_ID with actual ID from list
curl -X POST "http://localhost:5245/api/workers/WORKER_ID/disable" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"reason":"Testing authorization"}'
```

**Expected:** Admin succeeds, non-admin gets 403

---

### 10. Logout

**Objective:** Verify logout clears authentication state

**Steps:**
1. Login as any user
2. Verify logged in (check localStorage for token)
3. Click user menu → Logout (or navigate to logout route)
4. Check authentication state

**Expected Results:**
- ✅ Token removed from localStorage
- ✅ AuthContext user set to null
- ✅ isAuthenticated = false
- ✅ Redirect to /login
- ✅ Cannot access protected routes

**Verification:**
```javascript
// In browser console
localStorage.getItem('auth-token') // Should be null
```

---

### 11. Token Expiration & 401 Handling

**Objective:** Verify expired/invalid tokens are handled correctly

**Steps:**
1. Login and get valid token
2. Corrupt the token in localStorage:
   ```javascript
   // In browser console
   localStorage.setItem('auth-token', 'invalid.jwt.token')
   ```
3. Navigate to any protected page or make API request
4. Check behavior

**Expected Results:**
- ✅ API returns 401 Unauthorized
- ✅ Axios interceptor catches 401
- ✅ Token cleared from localStorage
- ✅ Redirect to /login
- ✅ User can login again to get new token

---

### 12. Role-Based UI Rendering

**Objective:** Verify UI shows/hides features based on user role

**Test 12a: Admin User**
1. Login as `admin` (farm_admin)
2. Check navigation menu

**Expected:**
- ✅ Shows "Admin" section in navigation
- ✅ Shows links to /admin/users, /admin/workers, etc.
- ✅ Shows "Slicer Profiles" link

**Test 12b: Regular User**
1. Login as `testuser` (farm_user)
2. Check navigation menu

**Expected:**
- ✅ Does NOT show "Admin" section
- ✅ Does NOT show admin links
- ✅ Shows regular user features (Dashboard, Printers, Files, etc.)

---

### 13. Inactive User Handling

**Objective:** Verify inactive users cannot login (pending approval)

**Steps:**
1. Manually create inactive user in database:
   ```sql
   -- Connect to database
   sqlite3 farm.db
   
   -- Create inactive user
   INSERT INTO Users (Id, Username, Email, PasswordHash, IsActive, EmailConfirmed, CreatedAt, UpdatedAt)
   VALUES (
     lower(hex(randomblob(16))),
     'inactiveuser',
     'inactive@test.com',
     '$2a$11$hashedpassword',
     0,
     0,
     datetime('now'),
     datetime('now')
   );
   ```

2. Try to login with inactive account
3. Check error message

**Expected Results:**
- ✅ Login fails
- ✅ Error message: "Your account is pending admin approval. You cannot log in until approved."
- ✅ No token stored
- ✅ User remains on login page

**Alternative:** Test via registration if new users are set to inactive by default.

---

### 14. Password Visibility Toggle

**Objective:** Verify password fields can be shown/hidden

**Steps:**
1. Navigate to /login
2. Enter password (field shows dots: ••••••••)
3. Click eye icon next to password field
4. Verify password visible
5. Click eye-off icon
6. Verify password hidden again

**Expected Results:**
- ✅ Password field starts as type="password"
- ✅ Eye icon click changes to type="text"
- ✅ Password characters visible when type="text"
- ✅ Eye-off icon click changes back to type="password"
- ✅ Works on both login and register forms
- ✅ Works on confirm password field

---

### 15. Form Validation - Registration

**Objective:** Verify client-side validation on registration form

**Test 15a: Username Too Short**
- Enter username with < 3 characters
- Submit form
- **Expected:** Error message: "Username must be at least 3 characters long"

**Test 15b: Invalid Email**
- Enter email without @ or domain (e.g., "testuser")
- Submit form
- **Expected:** Error message: "Please enter a valid email address"

**Test 15c: Password Too Short**
- Enter password with < 6 characters
- Submit form
- **Expected:** Error message: "Password must be at least 6 characters long"

**Test 15d: Passwords Don't Match**
- Enter password: "Test123!"
- Enter confirm password: "Different123!"
- Submit form
- **Expected:** Error message: "Passwords do not match"

---

### 16. hasRole() and hasPermission() Hooks

**Objective:** Verify useAuth hooks correctly check roles/permissions

**Test in Browser Console:**
```javascript
// Assuming you have React DevTools installed
// Login as admin first, then:

// Access AuthContext from React DevTools or console
const auth = window.__REACT_DEVTOOLS_GLOBAL_HOOK__?.renderers?.get(1)?.getOwnerStack()

// Test hasRole
auth.hasRole('farm_admin') // Should return true
auth.hasRole('farm_user')  // Should return true (admin has all roles)
auth.hasRole('nonexistent') // Should return false

// Test hasPermission
auth.hasPermission('printers', 'admin') // Should return true (admin bypass)
auth.hasPermission('users', 'read')     // Should return true
auth.hasPermission('invalid', 'action') // Should return true (admin bypass)
```

**Alternative:** Add console.log statements in components that use these hooks.

---

## Automated Test Scenarios (Future)

### API Integration Tests (.NET)
**File:** `src/tests/Farm.Web.Api.Tests/AuthenticationTests.cs`

**Test Cases:**
1. `POST /api/auth/register` - Creates user with default role
2. `POST /api/auth/login` - Returns JWT token for valid credentials
3. `POST /api/auth/login` - Returns 401 for invalid credentials
4. `GET /api/auth/me` - Returns current user with valid token
5. `GET /api/auth/me` - Returns 401 without token
6. `POST /api/auth/logout` - Clears authentication
7. `GET /api/printers` - Requires authentication
8. `POST /api/slicer/profiles/import` - Requires farm_admin role
9. `POST /api/workers/{id}/disable` - Requires farm_admin role
10. `DELETE /api/slicer/profiles/{id}` - Requires farm_admin role

### React Component Tests (Vitest)
**File:** `src/Web/ReactApp/src/test/auth/AuthContext.test.tsx`

**Test Cases:**
1. AuthProvider initializes with no user when no token
2. AuthProvider loads user when valid token in localStorage
3. login() updates user state and stores token
4. logout() clears user state and removes token
5. hasRole() correctly identifies user roles
6. hasPermission() correctly checks permissions
7. hasPermission() returns true for admin users (bypass)

**File:** `src/Web/ReactApp/src/test/auth/ProtectedRoute.test.tsx`

**Test Cases:**
1. Shows loading state while checking auth
2. Redirects unauthenticated users
3. Shows "Authentication Required" for guests
4. Renders children for authenticated users
5. Blocks users without required role
6. Blocks users without required permission
7. Allows admin users through all checks

**File:** `src/Web/ReactApp/src/test/auth/LoginModal.test.tsx`

**Test Cases:**
1. Renders login form with username and password fields
2. Validates required fields
3. Shows/hides password with eye icon
4. Displays error message on failed login
5. Calls login function from AuthContext
6. Disables inputs during submission

---

## Test Results Tracking

| Test # | Scenario | Status | Notes |
|--------|----------|--------|-------|
| 1 | First-Run Setup | ⏳ | |
| 2 | User Registration | ⏳ | |
| 3 | Login - Valid | ⏳ | |
| 4 | Login - Invalid | ⏳ | |
| 5 | Unauthenticated Access | ⏳ | |
| 6 | Non-Admin Access | ⏳ | |
| 7 | Admin Access | ⏳ | |
| 8 | API Authorization | ⏳ | |
| 9 | Worker Management | ⏳ | |
| 10 | Logout | ⏳ | |
| 11 | Token Expiration | ⏳ | |
| 12 | Role-Based UI | ⏳ | |
| 13 | Inactive User | ⏳ | |
| 14 | Password Toggle | ⏳ | |
| 15 | Form Validation | ⏳ | |
| 16 | useAuth Hooks | ⏳ | |

**Legend:**
- ⏳ Pending
- ✅ Pass
- ❌ Fail
- 🔄 In Progress

---

## Known Issues & Limitations

1. **Token Refresh:** Current implementation uses 7-day JWT tokens without refresh. Future enhancement: implement refresh token rotation.

2. **Password Reset:** No password reset flow implemented. Users must contact admin to reset password.

3. **Two-Factor Authentication:** Not implemented. Consider for future security enhancement.

4. **Account Lockout:** No automatic lockout after failed login attempts. Consider for future security enhancement.

5. **Email Verification:** Email confirmation flag exists but no email sending implemented.

---

## Troubleshooting

### Issue: "401 Unauthorized" on every request
**Solution:** Check that JWT middleware is configured in Program.cs and token is being attached by axios interceptor.

### Issue: "403 Forbidden" for admin endpoints
**Solution:** Verify user has `farm_admin` role in database and JWT includes role claims.

### Issue: Redirect loop on /login
**Solution:** Check axios response interceptor doesn't redirect if already on /login page.

### Issue: Token not persisting across page refreshes
**Solution:** Verify token is stored in localStorage and AuthContext initializes from localStorage on mount.

### Issue: "Access Denied" for authenticated user
**Solution:** Check ProtectedRoute `requiredRole` matches user's roles from JWT.

---

**Test Plan Version:** 1.0  
**Last Updated:** 2025-01-09  
**Prepared By:** GitHub Copilot
