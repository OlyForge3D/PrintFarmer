# PrintFarmer - PostgreSQL Development Environment
#
# This file sets environment variables for using PostgreSQL as the database provider.
# Source this file before running start-all-local-with-workers.sh, or add these exports to your shell profile for persistent effect.

export DB_PROVIDER=Postgres
export ConnectionStrings__Postgres="Host=localhost;Database=printfarmer;Username=postgres;Password=postgres"
