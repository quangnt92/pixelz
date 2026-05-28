using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pixelz.Application.Orders.EventHandlers;
using Pixelz.Domain.Common;
using Pixelz.Domain.Orders.Events;

namespace Pixelz.Infrastructure.Outbox;

// ── Entity ────────────────────────────────────────────────────────────────────

public class OutboxMessage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;
    public int RetryCount { get; private set; }
    public int MaxRetries { get; private set; } = 5;
    public DateTime NextRetryAt { get; private set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    private OutboxMessage() { }

    public static OutboxMessage Create(string eventType, IDomainEvent payload) =>
        new()
        {
            EventType = eventType,
            Payload   = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions)
        };

    public IDomainEvent Deserialize() => EventType switch
    {
        nameof(CheckoutSucceededEvent) => JsonSerializer.Deserialize<CheckoutSucceededEvent>(Payload, JsonOptions)!,
        nameof(PaymentFailedEvent) => JsonSerializer.Deserialize<PaymentFailedEvent>(Payload, JsonOptions)!,
        _ => throw new InvalidOperationException($"No deserializer for event type: {EventType}")
    };

    public void MarkAsProcessed()
    {
        Status = OutboxStatus.Processed;
        ProcessedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string error)
    {
        RetryCount++;
        ErrorMessage = error.Length > 2000 ? error[..2000] : error;
        Status = RetryCount >= MaxRetries ? OutboxStatus.Failed : OutboxStatus.Pending;

        // Exponential backoff: 2^retryCount minutes, capped at 60 minutes
        var delayMinutes = Math.Min(Math.Pow(2, RetryCount), 60);
        NextRetryAt = DateTime.UtcNow.AddMinutes(delayMinutes);
    }
}

public enum OutboxStatus : byte
{
    Pending   = 0,
    Processed = 2,
    Failed    = 3   // Max retries exceeded
}

// ── Background Processor ──────────────────────────────────────────────────────

/// <summary>
/// Polls OutboxMessages every 5 seconds and dispatches them to their handlers.
/// Runs inside a scoped DI scope so handlers get fresh DbContext per batch.
/// </summary>
public class OutboxProcessor(IServiceProvider serviceProvider, ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxProcessor started. Polling every {Interval}s", PollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in OutboxProcessor. Will retry next poll.");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        logger.LogInformation("OutboxProcessor stopped.");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.PixelzDbContext>();
        var checkoutHandler = scope.ServiceProvider.GetRequiredService<CheckoutSucceededEventHandler>();

        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.NextRetryAt <= DateTime.UtcNow && m.RetryCount < m.MaxRetries)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (messages.Count == 0) return;

        logger.LogDebug("OutboxProcessor: dispatching {Count} message(s)", messages.Count);

        foreach (var msg in messages)
        {
            try
            {
                var domainEvent = msg.Deserialize();

                switch (domainEvent)
                {
                    case CheckoutSucceededEvent e:
                        await checkoutHandler.HandleAsync(e, ct);
                        break;

                    default:
                        logger.LogWarning("No handler for OutboxMessage EventType={EventType} Id={Id}", msg.EventType, msg.Id);
                        break;
                }

                msg.MarkAsProcessed();
                logger.LogInformation("OutboxMessage processed. Id={Id} EventType={EventType}", msg.Id, msg.EventType);
            }
            catch (Exception ex)
            {
                msg.MarkAsFailed(ex.Message);
                logger.LogError(ex,"OutboxMessage failed. Id={Id} EventType={EventType} Retry={Retry}/{Max}", msg.Id, msg.EventType, msg.RetryCount, msg.MaxRetries);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}