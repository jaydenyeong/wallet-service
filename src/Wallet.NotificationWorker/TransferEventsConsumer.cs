using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Wallet.Contracts;
using Wallet.NotificationWorker.Data;

namespace Wallet.NotificationWorker;

public sealed class TransferEventsConsumer(
    IServiceScopeFactory scopeFactory, 
    IConfiguration config, 
    ILogger<TransferEventsConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);
    
    private async Task RunAsync(CancellationToken ct)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            GroupId = config["Kafka:GroupId"] ?? "notification-worker",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(Topics.Transfers);
        logger.LogInformation("Subscribed to {Topic}", Topics.Transfers);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = consumer.Consume(ct);

                TransferCompleted? evt = null;
                try
                {
                    evt = JsonSerializer.Deserialize<TransferCompleted>(result.Message.Value, JsonSerializerOptions.Web);
                }
                catch (JsonException ex)
                {
                    logger.LogError(ex, "Poitson message at {Offset}, skipping", result.TopicPartitionOffset);
                }

                if (evt is not null)
                    await HandleWithRetryAsync(evt, ct);
                
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException) {/* shutting down */}
        finally
        {
            consumer.Close();
        }
    }

    private async Task HandleWithRetryAsync(TransferCompleted evt, CancellationToken ct)
    {
        while (true)
        {
            try{
                await HandleAsync(evt, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to handle {EventId}, retrying in 5s", evt.EventId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleAsync(TransferCompleted evt, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO processed_events (event_id, processed_at) VALUES ({evt.EventId}, {now}) ON CONFLICT DO NOTHING", ct);
        
        if (inserted == 0)
        {
            logger.LogInformation("Duplicate event {EventId}, skipping", evt.EventId);
            return;
        }
        
        var amount = $"RM {evt.Amount / 100m:N2}";
        var note = string.IsNullOrWhiteSpace(evt.Note) ? "" : $"({evt.Note})";

        db.Notifications.AddRange(
            new Notification
            {
                Id = Guid.CreateVersion7(), UserId = evt.FromUserId, EventId = evt.EventId, CreatedAt = now,
                Message = $"You sent {amount} to {evt.ToEmail}{note}",
            },
            new Notification
            {
                Id = Guid.CreateVersion7(), UserId = evt.ToUserId, EventId = evt.EventId, CreatedAt = now,
                Message = $"You received {amount} from {evt.FromEmail}{note}",
            }
        );

        await db.SaveChangesAsync();
        await tx.CommitAsync(ct);

        logger.LogInformation("Notified {FromEmail} and {ToEmail} about transfer {TransferId}",
            evt.FromEmail, evt.ToEmail, evt.TransferId);
    }
}