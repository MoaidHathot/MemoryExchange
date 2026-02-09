using System.Text.RegularExpressions;

namespace MemoryExchange.Core.Chunking;

/// <summary>
/// Parses MemoryExchangeManagement.md to build a mapping from code directory patterns
/// to memory exchange domains. Used for domain boosting at query time.
/// </summary>
public class DomainRoutingMap
{
    private readonly List<(string Domain, string[] Patterns)> _mappings = new();

    /// <summary>
    /// All known domain names in the map.
    /// </summary>
    public IReadOnlyList<string> Domains => _mappings.Select(m => m.Domain).ToList();

    /// <summary>
    /// Parses the MemoryExchangeManagement.md content to extract domain-to-directory mappings.
    /// Expected format is a YAML code block with domain_coverage mapping.
    /// </summary>
    public static DomainRoutingMap Parse(string managementFileContent)
    {
        var map = new DomainRoutingMap();

        // Extract the YAML block
        var yamlMatch = Regex.Match(managementFileContent,
            @"```ya?ml\s*\n(.*?)```",
            RegexOptions.Singleline);

        if (!yamlMatch.Success)
            return map;

        var yamlContent = yamlMatch.Groups[1].Value;

        // Parse each domain line: "  domain: ['path1/', 'path2/']"
        var lineRegex = new Regex(@"^\s+(\w+):\s*\[([^\]]+)\]",
            RegexOptions.Multiline);

        foreach (Match match in lineRegex.Matches(yamlContent))
        {
            var domain = match.Groups[1].Value;
            var pathsRaw = match.Groups[2].Value;

            // Parse individual paths from the list: 'path1/', 'path2/'
            var paths = Regex.Matches(pathsRaw, @"'([^']+)'")
                .Select(m => m.Groups[1].Value)
                .ToArray();

            if (paths.Length > 0)
                map._mappings.Add((domain, paths));
        }

        return map;
    }

    /// <summary>
    /// Determines the domain for a memory exchange file based on its relative path.
    /// For files under domains/xx/, returns "xx".
    /// For root files, returns "root".
    /// </summary>
    public static string GetDomainFromFilePath(string relativeFilePath)
    {
        var normalized = relativeFilePath.Replace('\\', '/').TrimStart('/');

        if (normalized.StartsWith("domains/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/');
            if (parts.Length >= 2)
                return parts[1];
        }

        return "root";
    }

    /// <summary>
    /// Given a code file path (from the working repository), determines which
    /// memory exchange domains are relevant. Used for domain boosting in search.
    /// </summary>
    /// <param name="codeFilePath">Path of the code file being edited.</param>
    /// <returns>List of relevant domain names, ordered by specificity.</returns>
    public List<string> GetDomainsForCodePath(string codeFilePath)
    {
        var normalized = codeFilePath.Replace('\\', '/');
        var matchedDomains = new List<string>();

        foreach (var (domain, patterns) in _mappings)
        {
            foreach (var pattern in patterns)
            {
                if (MatchesPattern(normalized, pattern))
                {
                    matchedDomains.Add(domain);
                    break;
                }
            }
        }

        return matchedDomains;
    }

    /// <summary>
    /// Checks if a file path matches a directory pattern.
    /// Supports simple prefix matching and ** glob patterns.
    /// </summary>
    private static bool MatchesPattern(string filePath, string pattern)
    {
        // Convert glob pattern to simple matching
        // "src/ResourceProvider/" -> prefix match
        // "src/**/*Tests/" -> contains "Tests/" anywhere under src/
        var normalized = pattern.Replace('\\', '/');

        if (normalized.Contains("**"))
        {
            // Extract the parts before and after **
            var parts = normalized.Split("**", 2);
            var prefix = parts[0].TrimEnd('/');
            var suffix = parts.Length > 1 ? parts[1].TrimStart('/').TrimEnd('/') : "";

            bool matchesPrefix = string.IsNullOrEmpty(prefix) || 
                filePath.Contains(prefix, StringComparison.OrdinalIgnoreCase);
            bool matchesSuffix = string.IsNullOrEmpty(suffix) || 
                filePath.Contains(suffix, StringComparison.OrdinalIgnoreCase);

            return matchesPrefix && matchesSuffix;
        }
        else
        {
            // Simple prefix/contains match
            return filePath.Contains(normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
