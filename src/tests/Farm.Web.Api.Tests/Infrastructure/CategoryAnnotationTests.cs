using System.Text.RegularExpressions;

namespace Farm.Web.Api.Tests.Infrastructure;

public class CategoryAnnotationTests
{
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "farm-web.sln")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root (farm-web.sln or .git) not found from current directory.");
    }

    private static IEnumerable<string> GetTestFiles()
    {
        string repoRoot = FindRepoRoot();
        string testsRoot = Path.Combine(repoRoot, "src", "tests");
        if (!Directory.Exists(testsRoot))
        {
            yield break;
        }
        foreach (string f in Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            yield return f;
        }
    }

    [Fact(DisplayName = "Presubmit: Docker tests must be tagged with Trait(\"Category\", \"Docker\")")]
    public void DockerTestsAreTagged()
    {
        Regex dockerPatterns = new Regex(@"\b(RunDockerComposeCommandAsync|RunDockerCommandAsync|DockerTestHelpers|docker(?:-|\s)compose|docker-compose|docker compose)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        Regex traitPattern = new Regex(@"Trait\s*\(\s*""Category""\s*,\s*""Docker""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        List<string> offenders = new List<string>();

        foreach (string file in GetTestFiles())
        {
            string content = File.ReadAllText(file);
            if (dockerPatterns.IsMatch(content))
            {
                if (!traitPattern.IsMatch(content))
                {
                    offenders.Add(file);
                }
            }
        }

        if (offenders.Any())
        {
            string message = "The following test files invoke Docker or docker-compose but are not tagged with [Trait(\"Category\", \"Docker\")]:\n"
                          + string.Join("\n", offenders.Select(p => " - " + Path.GetRelativePath(Directory.GetCurrentDirectory(), p)));
            message += "\n\nPlease add [Trait(\"Category\", \"Docker\")] to each file so CI can exclude Docker tests from fast runs.";
            throw new Xunit.Sdk.XunitException(message);
        }
    }
}
