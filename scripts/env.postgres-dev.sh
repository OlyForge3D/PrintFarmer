# PrintFarmer - PostgreSQL Development Environment
#
# This file sets environment variables for using PostgreSQL as the database provider.
# Source this file before running start-all-local-with-workers.sh, or add these exports to your shell profile for persistent effect.

export DB_PROVIDER=Postgres
# Canonical connection string used by the application and deploy scripts
# Keep provider-specific var for backward compatibility, but ensure the
# unified `ConnectionStrings__Default` is also exported so all entry points
# (local dev, deploy-docker.sh, and tests) read the same variable.
export ConnectionStrings__Postgres="Host=localhost;Database=printfarmer;Username=postgres;Password=postgres"
export ConnectionStrings__Default="$ConnectionStrings__Postgres"
