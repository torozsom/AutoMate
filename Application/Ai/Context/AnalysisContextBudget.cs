using System.Text;
using System.Text.Json;

namespace Application.Ai;

/// <summary>
///     Consistent context limits, including conservative provider-input token accounting.
/// </summary>
/// <param name="Characters">The maximum unencoded context character count.</param>
/// <param name="Bytes">The maximum encoded UTF-8 size of provider input.</param>
/// <param name="Tokens">The maximum conservative provider-input token-unit count.</param>
public sealed record AnalysisContextBudget(int Characters, int Bytes, int Tokens)
{
    /// <summary>
    ///     Clamps configured limits to bounded memory usage; invalid tiny limits yield empty
    ///     context.
    /// </summary>
    /// <param name="options">The current validated options used by this operation.</param>
    /// <returns>A clamped context budget derived from the current options.</returns>
    public static AnalysisContextBudget From(AiAnalysisOptions options)
    {
        return new AnalysisContextBudget(
            Math.Clamp(options.MaximumContextCharacters, 1, 131072),
            Math.Clamp(options.MaximumContextBytes, 1, 131072),
            Math.Clamp(options.MaximumContextTokens, 1, 131072));
    }

    /// <summary>
    ///     JSON escaping makes diagnostics data, including fake closing delimiters, remain a string
    ///     value.
    /// </summary>
    /// <param name="text">The diagnostic context or text being processed.</param>
    /// <returns>
    ///     JSON with diagnostic text encoded as a string value, including any apparent delimiter
    ///     text.
    /// </returns>
    public static string ProviderInput(string text)
    {
        return JsonSerializer.Serialize(new { diagnostics = text });
    }

    /// <summary>
    ///     Charges one token per UTF-8 byte of encoded provider input; this is an upper bound, not
    ///     a tokenizer.
    /// </summary>
    /// <param name="text">The diagnostic context or text being processed.</param>
    /// <returns>Whether encoded provider input fits all three configured context bounds.</returns>
    public bool Fits(string text)
    {
        if (text.Length > Characters) return false;
        var encodedBytes = Encoding.UTF8.GetByteCount(ProviderInput(text));
        return encodedBytes <= Bytes && encodedBytes <= Tokens;
    }
}