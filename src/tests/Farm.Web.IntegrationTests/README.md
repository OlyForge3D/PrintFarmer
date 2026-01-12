Integration tests (opt-in)
==========================

By default the repository does NOT run integration tests during a normal
`dotnet test` or CI job. Integration tests are heavier and may require
Docker or long-running services. Use the MSBuild property or environment
variable `RunIntegrationTests` to opt-in.

How to run (zsh)
-----------------

# Option 1 — run the integration project only (recommended for local runs)
RunIntegrationTests=true dotnet test ./src/tests/Farm.Web.IntegrationTests/Farm.Web.IntegrationTests.csproj

# Option 2 — enable integration tests when running the full solution
RunIntegrationTests=true dotnet test ./src/farm-web.sln

# Alternatively you can export the variable in your shell for multiple commands
export RunIntegrationTests=true
dotnet test ./src/farm-web.sln

Notes
-----
- The `RunIntegrationTests` MSBuild property is read by the integration test
  project. When it is not `true` the project is excluded from the normal
  test run (the project sets `<IsTestProject Condition="'$(RunIntegrationTests)'!='true'">false</IsTestProject>`).
- Some integration tests require Docker (slicer engine containers, orca/prusa
  worker integration). Make sure Docker Desktop / Docker Engine is running and
  you have sufficient resources before enabling those tests.
- Use the scripts in `scripts/` (for example `scripts/start-all-local.sh`) if
  you need to bring up supporting services locally; those scripts are optional
  and documented elsewhere in the repo.
- Integration tests can be slower and may need increased timeouts in CI.

If you want, I can add a short CI job example that conditionally runs
integration tests when a pipeline parameter is provided.
