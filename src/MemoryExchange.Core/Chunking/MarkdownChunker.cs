using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MemoryExchange.Core.Models;

namespace MemoryExchange.Core.Chunking;

/// <summary>
/// Splits markdown files into semantically meaningful chunks for indexing.
/// Uses heading-aware splitting with code block preservation.
/// </summary>
public static class MarkdownChunker
{
    /// <summary>
    /// Approximate max tokens per chunk. We use a rough char-to-token ratio of 4:1.
    /// ~500 tokens = ~2000 characters.
    /// </summary>
    private const int MaxChunkChars = 2000;

    /// <summary>
    /// Minimum chunk size to avoid creating tiny fragments.
    /// </summary>
    private const int MinChunkChars = 100;

    // Regex patterns for tag extraction
    private static readonly Regex BacktickTermRegex = new(@"`([A-Z][A-Za-z0-9_.]+)`", RegexOptions.Compiled);
    private static readonly Regex FilePathRegex = new(@"(?:^|[\s`""])([A-Za-z0-9_./-]+\.[a-z]{1,5})(?:[\s`""]|$)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownLinkRegex = new(@"\[.*?\]\(([^)]+\.md)\)", RegexOptions.Compiled);

    /// <summary>
    /// Chunks a markdown file into a list of MemoryChunk objects.
    /// </summary>
    /// <param name="content">Raw markdown content.</param>
    /// <param name="relativeFilePath">Relative path of the file within the source directory.</param>
    /// <param name="domain">Domain identifier (e.g., "da", "rp").</param>
    /// <returns>List of chunks ready for embedding and indexing.</returns>
    public static List<MemoryChunk> ChunkFile(string content, string relativeFilePath, string domain)
    {
        var chunks = new List<MemoryChunk>();
        var isInstruction = relativeFilePath.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase);
        var sections = SplitIntoSections(content);

        int chunkIndex = 0;
        foreach (var section in sections)
        {
            var subChunks = SplitSectionIntoChunks(section.Content, section.HeadingPath);
            foreach (var subChunk in subChunks)
            {
                if (subChunk.Length < MinChunkChars)
                    continue;

                var chunk = new MemoryChunk
                {
                    Id = GenerateChunkId(relativeFilePath, chunkIndex),
                    Content = subChunk,
                    SourceFile = NormalizePath(relativeFilePath),
                    HeadingPath = section.HeadingPath,
                    Domain = domain,
                    Tags = ExtractTags(subChunk),
                    RelatedFiles = ExtractRelatedFiles(subChunk),
                    IsInstruction = isInstruction,
                    LastUpdated = DateTimeOffset.UtcNow,
                    ChunkIndex = chunkIndex
                };

                chunks.Add(chunk);
                chunkIndex++;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Splits markdown content into sections based on headings.
    /// Each section includes its heading path for context.
    /// </summary>
    private static List<MarkdownSection> SplitIntoSections(string content)
    {
        var sections = new List<MarkdownSection>();
        var lines = content.Split('\n');
        var headingStack = new List<(int Level, string Text)>();
        var currentContent = new StringBuilder();
        string currentHeadingPath = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var headingMatch = ParseHeading(line);

            if (headingMatch.HasValue)
            {
                // Flush the current section
                if (currentContent.Length > 0)
                {
                    sections.Add(new MarkdownSection(currentHeadingPath, currentContent.ToString().Trim()));
                    currentContent.Clear();
                }

                // Update heading stack
                var (level, text) = headingMatch.Value;
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((level, text));

                currentHeadingPath = string.Join(" > ", headingStack.Select(h => h.Text));

                // Include the heading itself in the chunk content for context
                currentContent.AppendLine(line);
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        // Flush final section
        if (currentContent.Length > 0)
        {
            sections.Add(new MarkdownSection(currentHeadingPath, currentContent.ToString().Trim()));
        }

        return sections;
    }

    /// <summary>
    /// Splits a section into sub-chunks if it exceeds MaxChunkChars,
    /// keeping code blocks atomic with their preceding explanation.
    /// </summary>
    private static List<string> SplitSectionIntoChunks(string sectionContent, string headingPath)
    {
        if (sectionContent.Length <= MaxChunkChars)
            return [sectionContent];

        var chunks = new List<string>();
        var blocks = SplitIntoBlocks(sectionContent);
        var currentChunk = new StringBuilder();

        foreach (var block in blocks)
        {
            // If a single block exceeds max, we still add it as-is (don't break code blocks)
            if (block.Length > MaxChunkChars && currentChunk.Length == 0)
            {
                chunks.Add(block);
                continue;
            }

            // Would adding this block exceed the limit?
            if (currentChunk.Length + block.Length + 2 > MaxChunkChars && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }

            if (currentChunk.Length > 0)
                currentChunk.AppendLine();
            currentChunk.Append(block);
        }

        if (currentChunk.Length > 0)
            chunks.Add(currentChunk.ToString().Trim());

        return chunks;
    }

    /// <summary>
    /// Splits content into logical blocks: paragraphs, code blocks (with preceding line),
    /// list items, etc. Code blocks are kept atomic with their preceding explanation paragraph.
    /// </summary>
    private static List<string> SplitIntoBlocks(string content)
    {
        var blocks = new List<string>();
        var lines = content.Split('\n');
        var currentBlock = new StringBuilder();
        bool inCodeBlock = false;
        string? precedingParagraph = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    // Starting a code block — save preceding paragraph
                    if (currentBlock.Length > 0)
                    {
                        precedingParagraph = currentBlock.ToString().Trim();
                        currentBlock.Clear();
                    }

                    inCodeBlock = true;
                    currentBlock.AppendLine(line);
                }
                else
                {
                    // Ending a code block — combine with preceding paragraph
                    currentBlock.AppendLine(line);
                    inCodeBlock = false;

                    var codeBlock = currentBlock.ToString().Trim();
                    if (precedingParagraph != null)
                    {
                        blocks.Add(precedingParagraph + "\n" + codeBlock);
                        precedingParagraph = null;
                    }
                    else
                    {
                        blocks.Add(codeBlock);
                    }
                    currentBlock.Clear();
                }
            }
            else if (inCodeBlock)
            {
                currentBlock.AppendLine(line);
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                // Empty line = paragraph break
                if (currentBlock.Length > 0)
                {
                    if (precedingParagraph != null)
                    {
                        blocks.Add(precedingParagraph);
                        precedingParagraph = null;
                    }
                    blocks.Add(currentBlock.ToString().Trim());
                    currentBlock.Clear();
                }
            }
            else
            {
                currentBlock.AppendLine(line);
            }
        }

        // Flush remaining
        if (currentBlock.Length > 0)
        {
            if (precedingParagraph != null)
                blocks.Add(precedingParagraph);
            blocks.Add(currentBlock.ToString().Trim());
        }
        else if (precedingParagraph != null)
        {
            blocks.Add(precedingParagraph);
        }

        return blocks;
    }

    /// <summary>
    /// Extracts tags from content: backtick-wrapped class/service names and file paths.
    /// </summary>
    private static List<string> ExtractTags(string content)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract backtick-wrapped terms that look like class/type names (PascalCase)
        foreach (Match match in BacktickTermRegex.Matches(content))
        {
            var term = match.Groups[1].Value;
            // Filter out common non-class terms
            if (term.Length > 2 && !term.Contains('/'))
                tags.Add(term);
        }

        // Extract file paths mentioned in the text
        foreach (Match match in FilePathRegex.Matches(content))
        {
            var path = match.Groups[1].Value;
            if (path.Contains('/') || path.Contains('.'))
                tags.Add(path);
        }

        return tags.ToList();
    }

    /// <summary>
    /// Extracts cross-referenced markdown files from links.
    /// </summary>
    private static List<string> ExtractRelatedFiles(string content)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MarkdownLinkRegex.Matches(content))
        {
            var path = match.Groups[1].Value;
            // Normalize: remove anchors, relative path prefixes
            path = path.Split('#')[0];
            if (!string.IsNullOrWhiteSpace(path))
                files.Add(NormalizePath(path));
        }

        return files.ToList();
    }

    /// <summary>
    /// Generates a deterministic chunk ID from file path and chunk index.
    /// </summary>
    private static string GenerateChunkId(string filePath, int chunkIndex)
    {
        var input = $"{NormalizePath(filePath)}::{chunkIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes file paths to use forward slashes.
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Parses a line to check if it's a markdown heading.
    /// Returns (level, text) or null.
    /// </summary>
    private static (int Level, string Text)? ParseHeading(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
            return null;

        int level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;

        if (level > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            return null;

        var text = trimmed[(level + 1)..].Trim();
        return (level, text);
    }

    private record MarkdownSection(string HeadingPath, string Content);
}
