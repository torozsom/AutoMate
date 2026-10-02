using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Diagnostics;

/// <summary>Stores already-redacted diagnostics and creates bounded analysis context from them.</summary>
public sealed class DeploymentDiagnosticStore(AutoMateDbContext dbContext) : IDeploymentDiagnosticStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        dbContext.DeploymentDiagnosticRecords.Add(new DeploymentDiagnosticRecord
        {
            DeploymentId = diagnosticEvent.DeploymentId, ProjectId = diagnosticEvent.ProjectId,
            TimestampUtc = diagnosticEvent.TimestampUtc, Source = diagnosticEvent.Source.ToString(),
            Kind = diagnosticEvent.Kind.ToString(), Severity = diagnosticEvent.Severity.ToString(),
            Message = diagnosticEvent.Message,
            AttributesJson = diagnosticEvent.Attributes is null ? null : JsonSerializer.Serialize(diagnosticEvent.Attributes),
            TraceId = diagnosticEvent.TraceId, SpanId = diagnosticEvent.SpanId, Sequence = diagnosticEvent.Sequence,
            Cursor = diagnosticEvent.Cursor, ExpiresAt = DateTimeOffset.UtcNow.Add(Retention)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        var records = await dbContext.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(item => item.DeploymentId == deploymentId && item.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(item => item.Severity == "Critical").ThenByDescending(item => item.Severity == "Error")
            .ThenByDescending(item => item.TimestampUtc).Take(300).ToListAsync(cancellationToken);
        var context = new StringBuilder();
        foreach (var item in records.OrderBy(item => item.TimestampUtc))
        {
            var line = $"[{item.TimestampUtc:O}] [{item.Severity}] [{item.Source}/{item.Kind}] {item.Message}\n";
            if (context.Length + line.Length > maximumCharacters) break;
            context.Append(line);
        }
        return context.ToString();
    }
}
