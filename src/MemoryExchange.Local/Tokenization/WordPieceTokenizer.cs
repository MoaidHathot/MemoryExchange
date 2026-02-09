using System.Reflection;
using System.Text.RegularExpressions;

namespace MemoryExchange.Local.Tokenization;

/// <summary>
/// A minimal BERT-compatible WordPiece tokenizer for all-MiniLM-L6-v2.
/// Uses the uncased BERT base vocabulary (30,522 tokens) embedded as a resource.
/// Produces [CLS] token_ids [SEP] sequences with attention masks and token type IDs.
/// </summary>
internal sealed class WordPieceTokenizer
{
    private const string UnknownToken = "[UNK]";
    private const string ClsToken = "[CLS]";
    private const string SepToken = "[SEP]";
    private const int MaxWordPieceLength = 200;

    private readonly Dictionary<string, int> _vocab;

    /// <summary>
    /// The vocabulary size.
    /// </summary>
    public int VocabSize => _vocab.Count;

    public WordPieceTokenizer()
    {
        _vocab = LoadEmbeddedVocabulary();
    }

    /// <summary>
    /// Encodes text into BERT input format: [CLS] tokens [SEP], truncated to maxLength.
    /// Returns parallel arrays of input IDs, attention mask, and token type IDs.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength)
    {
        var tokens = Tokenize(text);

        // Reserve 2 slots for [CLS] and [SEP]
        var maxTokens = maxLength - 2;
        if (tokens.Count > maxTokens)
        {
            tokens = tokens.GetRange(0, maxTokens);
        }

        // Build ID sequence: [CLS] token_ids [SEP] [PAD...]
        var seqLength = tokens.Count + 2;
        var inputIds = new long[maxLength];
        var attentionMask = new long[maxLength];
        var tokenTypeIds = new long[maxLength]; // All zeros for single-sequence

        inputIds[0] = _vocab[ClsToken];
        attentionMask[0] = 1;

        for (int i = 0; i < tokens.Count; i++)
        {
            inputIds[i + 1] = tokens[i];
            attentionMask[i + 1] = 1;
        }

        inputIds[tokens.Count + 1] = _vocab[SepToken];
        attentionMask[tokens.Count + 1] = 1;

        // Remaining positions are already 0 (padding)

        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// Tokenizes text into a list of vocabulary IDs using WordPiece subword splitting.
    /// Text is lowercased (uncased model).
    /// </summary>
    private List<int> Tokenize(string text)
    {
        var ids = new List<int>();
        var words = BasicTokenize(text);

        foreach (var word in words)
        {
            var subIds = WordPieceTokenize(word);
            ids.AddRange(subIds);
        }

        return ids;
    }

    /// <summary>
    /// Basic pre-tokenization: lowercase, strip accents, split on whitespace and punctuation.
    /// </summary>
    private static List<string> BasicTokenize(string text)
    {
        text = text.ToLowerInvariant();

        // Insert spaces around punctuation so it becomes its own token
        var sb = new System.Text.StringBuilder(text.Length + 32);
        foreach (var ch in text)
        {
            if (IsPunctuation(ch))
            {
                sb.Append(' ');
                sb.Append(ch);
                sb.Append(' ');
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// WordPiece subword tokenization for a single word.
    /// Greedily matches the longest prefix in the vocabulary, then continues with "##" prefixed subwords.
    /// </summary>
    private List<int> WordPieceTokenize(string word)
    {
        if (word.Length > MaxWordPieceLength)
        {
            return [_vocab[UnknownToken]];
        }

        var ids = new List<int>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            int? foundId = null;

            while (start < end)
            {
                var substr = word[start..end];
                if (start > 0)
                {
                    substr = "##" + substr;
                }

                if (_vocab.TryGetValue(substr, out var id))
                {
                    foundId = id;
                    break;
                }

                end--;
            }

            if (foundId is null)
            {
                // Entire word is unknown
                return [_vocab[UnknownToken]];
            }

            ids.Add(foundId.Value);
            start = end;
        }

        return ids;
    }

    private static bool IsPunctuation(char ch)
    {
        // BERT considers these as punctuation
        if ((ch >= 33 && ch <= 47) || (ch >= 58 && ch <= 64) ||
            (ch >= 91 && ch <= 96) || (ch >= 123 && ch <= 126))
        {
            return true;
        }

        return char.IsPunctuation(ch) || char.IsSymbol(ch);
    }

    private static Dictionary<string, int> LoadEmbeddedVocabulary()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("vocab.txt", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                "Embedded vocabulary resource 'vocab.txt' not found. " +
                "Ensure it is included as an EmbeddedResource in the project.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);

        var vocab = new Dictionary<string, int>();
        string? line;
        int index = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrEmpty(line))
            {
                vocab[line.Trim()] = index;
            }
            index++;
        }

        return vocab;
    }
}
