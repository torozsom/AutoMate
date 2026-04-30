namespace Services.LogStreaming;

public interface ILogStreamer
{
    Task StreamBuildLogsAsync(Guid projectId, string message);
}