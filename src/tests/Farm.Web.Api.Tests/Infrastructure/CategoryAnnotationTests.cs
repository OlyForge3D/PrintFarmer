using System.Text.RegularExpressions;

namespace Farm.Web.Api.Tests.Infrastructure
{
    public class CategoryAnnotationTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
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
            var repoRoot = FindRepoRoot();
            var testsRoot = Path.Combine(repoRoot, "src", "tests");
            if (!Directory.Exists(testsRoot))
            {
                yield break;
            }
            foreach (var f in Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
            {
                yield return f;
            }
        }

        [Fact(DisplayName = "Presubmit: Docker tests must be tagged with Trait(\"Category\", \"Docker\")")]
        public void DockerTestsAreTagged()
        {
            var dockerPatterns = new Regex(@"\b(RunDockerComposeCommandAsync|RunDockerCommandAsync|DockerTestHelpers|docker(?:-|\s)compose|docker-compose|docker compose)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var traitPattern = new Regex(@"Trait\s*\(\s*""Category""\s*,\s*""Docker""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var offenders = new List<string>();

            foreach (var file in GetTestFiles())
            {
                var content = File.ReadAllText(file);
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
                var message = "The following test files invoke Docker or docker-compose but are not tagged with [Trait(\"Category\", \"Docker\")]:\n"
                              + string.Join("\n", offenders.Select(p => " - " + Path.GetRelativePath(Directory.GetCurrentDirectory(), p)));
                message += "\n\nPlease add [Trait(\"Category\", \"Docker\")] to each file so CI can exclude Docker tests from fast runs.";
                throw new Xunit.Sdk.XunitException(message);
            }
        }
    }
}
