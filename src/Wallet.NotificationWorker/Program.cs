using Microsoft.EntityFrameworkCore;
using Wallet.NotificationWorker;
using Wallet.NotificationWorker.Data;

var builder = Host.CreateApplicationBuilder(args);

// 1. Register the DbContext, the same as in the API but pointing at the notifications database
builder.Services.AddDbContext<NotificationDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("NotificationsDb"))
    .UseSnakeCaseNamingConvention());

// 2. Register the background loop that consumes Kafka (you write it in step 3)
builder.Services.AddHostedService<TransferEventsConsumer>();

var host = builder.Build();

// 3. Create or update the tables on startup
await using (var scope = host.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<NotificationDbContext>().Database.MigrateAsync();
}

host.Run();
