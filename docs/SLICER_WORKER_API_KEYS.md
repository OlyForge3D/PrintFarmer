## Slicer worker key generation

`install.sh` and `scripts/deploy-docker.sh` generate a cryptographically random
shared registration key. Reinstalls and in-place upgrades preserve the existing
key. The installer aborts if no cryptographically secure random source is
available rather than writing a predictable fallback. The generated `.env`
contains:

```dotenv
WORKER_SHARED_API_KEY=<generated-secret>
```

`WORKER_SHARED_API_KEY` is the canonical deployment value. Compose templates
pass it to:

- the API or slicer host as `WorkerAuth__SharedKey`;
- every OrcaSlicer worker as `WorkerAuth__SharedKey`.

`WorkerAuth:SharedKey` is the only .NET configuration path. Each successful
registration receives a unique service GUID and separate per-service key. The
worker uses that returned key for lifecycle calls and worker-only job routes;
the bootstrap deployment key cannot impersonate a registered service.

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

The script preserves the bootstrap key and single-worker identity where
supported and writes secrets to the ignored `.env` file. Scaled replicas derive
distinct runtime identities rather than sharing one configured instance ID.
Do not commit `.env`.

### Provide a key manually

For deployments that do not use the deployment script, generate at least 32
random bytes and configure the same value on the API/slicer host and workers:

```dotenv
WORKER_SHARED_API_KEY=<random-secret>
```

The equivalent .NET configuration variable for both sides is:

```dotenv
WorkerAuth__SharedKey=<random-secret>
```

The bootstrap value is not sent to worker-only job routes. Those routes use
the per-service key returned by successful registration.

### Startup requirements

When the slicer module is loaded, the API or slicer host refuses to start
without a bootstrap registration key. The worker performs the same startup
check. Configure `WorkerAuth:SharedKey` through environment variables, user
secrets, or the deployment secret store. Missing configuration fails startup
in every environment, including `Development` and `Testing`; there is no
authentication bypass.

Repository local launch scripts generate one ephemeral key for their API and
worker child processes without displaying or persisting it.

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
grep -E '^WORKER_SHARED_API_KEY=' .env |
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
