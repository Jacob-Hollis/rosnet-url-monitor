using Microsoft.EntityFrameworkCore;
using UrlMonitor.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<MonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Monitor")));

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
