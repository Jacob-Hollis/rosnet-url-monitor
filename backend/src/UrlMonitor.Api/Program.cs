using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Background;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<MonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Monitor")));

builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
builder.Services.AddUrlChecker();
builder.Services.AddScoped<ICheckRunner, CheckRunner>();

if (builder.Configuration.GetValue($"{MonitorOptions.SectionName}:{nameof(MonitorOptions.SchedulerEnabled)}", true))
{
    builder.Services.AddHostedService<CheckSchedulerService>();
}

var app = builder.Build();

await app.InitializeDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
