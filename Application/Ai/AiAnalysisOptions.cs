namespace Application.Ai;

public sealed class AiAnalysisOptions
{
    public const string SectionName = "AiAnalysis";
    public bool Enabled { get; init; }
    public bool AutomaticAnalysisEnabled { get; init; }
    public string Endpoint { get; init; } = "https://api.openai.com/v1/";
    public string Model { get; init; } = "gpt-5-mini";
    public int TimeoutSeconds { get; init; } = 60;
    public int MaximumContextCharacters { get; init; } = 24_000;
    public int MaximumConcurrency { get; init; } = 1;
    public int DailyProjectLimit { get; init; } = 5;
}