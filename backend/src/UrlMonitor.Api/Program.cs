using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Background;
using UrlMonitor.Api.Data;
using UrlMonitor.Api.Endpoints;
using UrlMonitor.Api.Services;
using UrlMonitor.Api.Services.Alerts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Enums go over the wire as strings ("Up", "Down", "Sent"...), matching how they're stored.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<MonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Monitor")));

builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection(MonitorOptions.SectionName));
builder.Services.AddUrlChecker();
builder.Services.AddScoped<ICheckRunner, CheckRunner>();

builder.Services.Configure<AlertsOptions>(builder.Configuration.GetSection(AlertsOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

if (builder.Configuration.GetValue($"{MonitorOptions.SectionName}:{nameof(MonitorOptions.SchedulerEnabled)}", true))
{
    builder.Services.AddHostedService<CheckSchedulerService>();
}

var app = builder.Build();

await app.InitializeDatabaseAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No HTTPS redirect: the app runs locally over plain HTTP behind the Vite dev proxy
// (design.md questions 19 and 20), and a redirect would break proxied /api calls.

app.MapUrlEndpoints();
app.MapCheckEndpoints();
app.MapAlertEndpoints();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
