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
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;
    public int RetryCount  { get; private set; }
    public int MaxRetries  { get; private set; } = 5;
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

    /// <summary>
    /// Deserializes the payload back to a domain event.
    /// Returns null for event types that have no downstream side effects
    /// (e.g. OrderCheckoutInitiatedEvent, PaymentFailedEvent).
    /// </summary>
    public IDomainEvent? TryDeserialize(ILogger logger)
    {
        try
        {
            return EventType switch
            {
                // Events that trigger downstream side effects — must be handled
                nameof(CheckoutSucceededEvent) =>
                    JsonSerializer.Deserialize<CheckoutSucceededEvent>(Payload, JsonOptions)!,

                // Events with no downstream handler — mark as processed and skip
                nameof(OrderCheckoutInitiatedEvent) => null,
                nameof(PaymentFailedEvent)           => null,

                // Unknown event type — log and skip instead of throwing
                _ => LogAndReturnNull(logger, EventType, Id)
            };
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "Failed to deserialize OutboxMessage. Id={Id} EventType={EventType}",
                Id, EventType);
            return null;
        }
    }

    private static IDomainEvent? LogAndReturnNull(ILogger logger, string eventType, Guid id)
    {
        logger.LogWarning(
            "OutboxMessage has no registered handler. Id={Id} EventType={EventType} — skipping.",
            id, eventType);
        return null;
    }

    public void MarkAsProcessed()
    {
        Status      = OutboxStatus.Processed;
        ProcessedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string error)
    {
        RetryCount++;
        ErrorMessage = error.Length > 2000 ? error[..2000] : error;
        Status       = RetryCount >= MaxRetries ? OutboxStatus.Failed : OutboxStatus.Pending;

        // Exponential backoff: 2^retryCount minutes, capped at 60 minutes
        var delayMinutes = Math.Min(Math.Pow(2, RetryCount), 60);
        NextRetryAt = DateTime.UtcNow.AddMinutes(delayMinutes);
    }
}

public enum OutboxStatus : byte
{
    Pending   = 0,
    Processed = 2,
    Failed    = 3
}

// ── Background Processor ──────────────────────────────────────────────────────

/// <summary>
/// Polls OutboxMessages every 5 seconds and dispatches them to their handlers.
/// Events with no handler (OrderCheckoutInitiatedEvent, PaymentFailedEvent)
/// are marked Processed immediately without error.
/// </summary>
public class OutboxProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxProcessor> _logger;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 20;

    public OutboxProcessor(IServiceProvider serviceProvider, ILogger<OutboxProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxProcessor started. Polling every {Interval}s", PollingInterval.TotalSeconds);

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
                _logger.LogError(ex, "Unhandled error in OutboxProcessor. Will retry next poll.");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("OutboxProcessor stopped.");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope         = _serviceProvider.CreateScope();
        var db                  = scope.ServiceProvider.GetRequiredService<Persistence.PixelzDbContext>();
        var checkoutHandler     = scope.ServiceProvider.GetRequiredService<CheckoutSucceededEventHandler>();

        var messages = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending
                     && m.NextRetryAt <= DateTime.UtcNow
                     && m.RetryCount  <  m.MaxRetries)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (messages.Count == 0)
            return;

        _logger.LogDebug("OutboxProcessor: processing {Count} message(s)", messages.Count);

        foreach (var msg in messages)
        {
            try
            {
                var domainEvent = msg.TryDeserialize(_logger);

                if (domainEvent is null)
                {
                    // No handler needed — mark processed silently
                    msg.MarkAsProcessed();
                    _logger.LogDebug(
                        "OutboxMessage skipped (no handler). Id={Id} EventType={EventType}",
                        msg.Id, msg.EventType);
                    continue;
                }

                switch (domainEvent)
                {
                    case CheckoutSucceededEvent e:
                        await checkoutHandler.HandleAsync(e, ct);
                        break;

                    default:
                        // Deserializer returned a known event but no switch case exists
                        // Mark processed to avoid infinite retry
                        _logger.LogWarning(
                            "No dispatch handler for EventType={EventType}. Marking processed.",
                            msg.EventType);
                        break;
                }

                msg.MarkAsProcessed();

                _logger.LogInformation(
                    "OutboxMessage processed. Id={Id} EventType={EventType}",
                    msg.Id, msg.EventType);
            }
            catch (Exception ex)
            {
                msg.MarkAsFailed(ex.Message);

                _logger.LogError(ex,
                    "OutboxMessage failed. Id={Id} EventType={EventType} Retry={Retry}/{Max}",
                    msg.Id, msg.EventType, msg.RetryCount, msg.MaxRetries);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
