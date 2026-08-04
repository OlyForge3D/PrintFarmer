## Slicer worker key generation

`install.sh` and `scripts/deploy-docker.sh` generate a cryptographically random
shared registration key. Reinstalls and in-place upgrades preserve the existing
key. The generated `.env` contains:

```dotenv
WORKER_SHARED_API_KEY=<generated-secret>
SlicerRegistry__ApiKey=<same-generated-secret>
```

`WORKER_SHARED_API_KEY` is the canonical deployment value. Compose templates
pass it to:

- the API or slicer host as `WorkerAuth__SharedKey`;
- OrcaSlicer workers as `Worker__SharedKey`.

`SlicerRegistry__ApiKey` is retained as a direct worker-configuration alias.
Scaled-worker aliases may also be emitted for compatibility, but resource
identity does not come from those aliases. Each successful registration
receives a unique service GUID and a separate per-service key. The worker uses
that returned key for both lifecycle calls and worker-only job routes; the
shared deployment key cannot impersonate another registered service.

See [Slicer worker authentication](WORKER_AUTHENTICATION.md) for the complete
header, route, ownership, and failure contracts.

### Generate keys

Run deployment from the repository root:

```bash
./scripts/deploy-docker.sh
```

The one-command installer configures the lite monolith automatically:

```bash
./install.sh --profile lite
```

For the full deployment script, enable OrcaSlicer workers when prompted. In
non-interactive environments, configure the worker count in `.deploy-config`
and run:

```bash
./scripts/deploy-docker.sh --non-interactive
```

The script preserves configured worker values where supported and writes
secrets to the ignored `.env` file. Do not commit that file.

### Provide a key manually

For deployments that do not use the deployment script, generate at least 32
random bytes and configure the same value on the API/slicer host and workers:

```dotenv
WORKER_SHARED_API_KEY=<random-secret>
```

Equivalent .NET configuration variables are:

```dotenv
WorkerAuth__SharedKey=<random-secret>
Worker__SharedKey=<same-random-secret>
```

The worker may instead set `SlicerRegistry__ApiKey` for registration. The
shared value is not sent to worker-only job routes; those routes use the
per-service key returned by successful registration.

### Startup requirements

When the slicer module is loaded, the API or slicer host refuses to start
without a shared registration key. Configure `WorkerAuth:SharedKey` through
environment variables, user secrets, or the deployment secret store. A missing
validator or invalid request key always returns `401`; missing configuration
never disables authentication implicitly.

Local development can explicitly opt into unauthenticated registration:

```dotenv
PFARM__WorkerAuth__AllowInsecureDevelopmentRegistration=true
```

This flag is accepted only when the host environment is `Development`. It is
rejected at startup in every other environment and emits a critical startup
log when active. The repository's local launch scripts set this opt-in for their
Development processes. Never configure it in a deployed environment.

### Verify configuration

After deployment:

1. Confirm the API/slicer host and worker are healthy.
2. Confirm worker logs report a successful registration and service GUID.
3. Confirm heartbeats succeed with no `authentication_required` responses.
4. Submit a slice job and confirm the registered worker can claim it.
5. Confirm a request without worker headers receives `401` Problem Details.

Do not print secret values while verifying. Check only that variables are
present:

```bash
grep -E '^(WORKER_SHARED_API_KEY|SlicerRegistry__ApiKey)=' .env |
  sed 's/=.*/=<configured>/'
```

### Rotate keys

1. Generate a replacement secret.
2. Update `WORKER_SHARED_API_KEY` for the API/slicer host and every worker.
3. Restart those services together.
4. Confirm registration returns a per-service key and that heartbeat and
   job-claim requests succeed with it.
5. Remove the old secret from the deployment secret store.

Changing only one side intentionally causes new registration to fail closed.
Registry-issued service keys are rotated independently through
`POST /api/slicers/{id}/rotate-key`; a worker must use the returned replacement
for all subsequent lifecycle and job requests.
