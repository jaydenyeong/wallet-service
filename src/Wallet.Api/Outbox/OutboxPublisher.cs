using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;

namespace Wallet.Api.Outbox;

public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IProducer<string, string> producer,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox publisher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox batch failed");
            }
            if (published == 0)
            {
                try {await Task.Delay(IdleDelay, stoppingToken);}
                catch (OperationCanceledException) {break;}
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.OutboxMessages
            .FromSql($"""
            SELECT * FROM outbox_messages
            WHERE published_at IS NULL
            ORDER BY occurred_at
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        
        if (batch.Count == 0) return 0;

        var published = 0;
        foreach (var msg in batch)
        {
            try
            {
                var result = await producer.ProduceAsync(msg.Topic, new Message<string, string>
                {
                    Key = msg.Key,
                    Value = msg.Payload,
                    Headers = new Headers
                    {
                        {"event-type", Encoding.UTF8.GetBytes(msg.Type)},
                        {"event-id", Encoding.UTF8.GetBytes(msg.Id.ToString())},
                    },
                }, ct);
                msg.PublishedAt = DateTimeOffset.UtcNow;
                published++;
                logger.LogInformation("Published {EventType} {EventId} to {TopicPartitionOffset}",
                    msg.Type, msg.Id, result.TopicPartitionOffset);
            }
            catch (ProduceException<string, string> ex)
            {
                msg.Attempts++;
                msg.LastError = ex.Error.Reason;
                logger.LogWarning("Failed to publish {EventId} (attemp {Attempts}): {Reason}",
                    msg.Id, msg.Attempts, ex.Error.Reason);
                break;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published;
    }
}