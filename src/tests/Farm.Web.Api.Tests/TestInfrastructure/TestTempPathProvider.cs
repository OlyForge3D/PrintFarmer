using Farm.Web.Api.Infrastructure.Temp;

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Test implementation that keeps all temp artifacts within the repository under src/tests/_temp/runtime
/// to avoid macOS TCC dialogs and ease cleanup.
/// </summary>
public sealed class TestTempPathProvider : ITempPathProvider
{
    private readonly string _root;

    public TestTempPathProvider()
    {
        _root = Path.Combine(TestPaths.RepoTempRoot, "runtime");
        _ = Directory.CreateDirectory(_root);
    }

    public string GetTempRoot() => _root;
}
