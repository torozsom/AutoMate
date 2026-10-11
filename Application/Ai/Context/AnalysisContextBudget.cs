using System.Text;
using System.Text.Json;

namespace Application.Ai;

/// <summary>Consistent context limits, including conservative provider-input token accounting.</summary>
public sealed record AnalysisContextBudget(int Characters, int Bytes, int Tokens)
{
    /// <summary>Clamps configured limits to bounded memory usage; invalid tiny limits yield empty context.</summary>
    public static AnalysisContextBudget From(AiAnalysisOptions options)
    {
        return new AnalysisContextBudget(
            Math.Clamp(options.MaximumContextCharacters, 1, 131072),
            Math.Clamp(options.MaximumContextBytes, 1, 131072),
            Math.Clamp(options.MaximumContextTokens, 1, 131072));
    }

    /// <summary>JSON escaping makes diagnostics data, including fake closing delimiters, remain a string value.</summary>
    public static string ProviderInput(string text)
    {
        return JsonSerializer.Serialize(new { diagnostics = text });
    }

    /// <summary>Charges one token per UTF-8 byte of encoded provider input; this is an upper bound, not a tokenizer.</summary>
    public bool Fits(string text)
    {
        if (text.Length > Characters) return false;
        var encodedBytes = Encoding.UTF8.GetByteCount(ProviderInput(text));
        return encodedBytes <= Bytes && encodedBytes <= Tokens;
    }
}