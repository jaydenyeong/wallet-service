using Microsoft.EntityFrameworkCore;
using Wallet.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<WalletDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("WalletDb"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Database.MigrateAsync();
}

app.Run();

public partial class Program;