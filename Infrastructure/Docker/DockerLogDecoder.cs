using System.Globalization;
using System.Text;

namespace Infrastructure.Docker;

/// <summary>A complete provider line or an explicit omission, before central redaction.</summary>
internal sealed record DockerLogLine(string Text, DateTimeOffset Timestamp, string? ProviderTimestamp, bool Omitted);

/// <summary>Incrementally decodes one UTF-8 stream with bounded partial lines and preserved CR/LF semantics.</summary>
internal sealed class DockerLogDecoder(bool timestamps, TimeProvider clock)
{
    /// <summary>Bounds raw partial text until a complete line can be centrally redacted.</summary>
    private const int MaximumLine = 8192;

    /// <summary>Retains incomplete UTF-8 code points between Docker frames.</summary>
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    /// <summary>Partial line for this stream only.</summary>
    private readonly StringBuilder _line = new();

    /// <summary>Whether a carriage return may be followed by a line feed in a later frame.</summary>
    private bool _carriageReturn;

    /// <summary>Whether an oversized line is being discarded through its delimiter.</summary>
    private bool _overflow;

    /// <summary>Returns completed lines without confusing byte chunks with character or line boundaries.</summary>
    internal IReadOnlyList<DockerLogLine> Feed(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, characters, flush);
        var output = new List<DockerLogLine>();
        for (var index = 0; index < count; index++)
        {
            var character = characters[index];
            if (_carriageReturn && character != '\n') Emit(output);
            if (!_overflow) _line.Append(character);
            if (_line.Length > MaximumLine)
            {
                _line.Clear();
                _overflow = true;
            }

            _carriageReturn = character == '\r';
            if (character == '\n') Emit(output);
        }

        if (flush && (_line.Length > 0 || _overflow)) Emit(output);
        return output;
    }

    /// <summary>Completes a bounded line and strips only a validated provider timestamp prefix.</summary>
    private void Emit(List<DockerLogLine> output)
    {
        var text = _overflow
            ? "[Docker output line omitted because it exceeded the safe line limit.]\r\n"
            : _line.ToString();
        string? providerTimestamp = null;
        var timestamp = clock.GetUtcNow();
        var separator = text.IndexOf(' ');
        if (timestamps && separator is > 0 and <= 40 && DateTimeOffset.TryParse(text.AsSpan(0, separator),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = parsed.ToUniversalTime();
            providerTimestamp = text[..separator];
            text = text[(separator + 1)..];
        }

        output.Add(new DockerLogLine(text.Length == 0 ? "\r\n" : text, timestamp, providerTimestamp, _overflow));
        _line.Clear();
        _overflow = false;
        _carriageReturn = false;
    }
}