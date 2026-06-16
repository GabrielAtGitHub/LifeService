using LifeService.Api.Endpoints;
using LifeService.Api.Middleware;
using LifeService.Application;
using LifeService.Domain.Configuration;
using LifeService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI (developer-facing API surface).
builder.Services.AddOpenApi();

// Bind configuration options (SYSTEM_SPECIFICATION.md §6).
builder.Services.Configure<LifeLimitsOptions>(
    builder.Configuration.GetSection(LifeLimitsOptions.SectionName));
builder.Services.Configure<LifeComputeOptions>(
    builder.Configuration.GetSection(LifeComputeOptions.SectionName));
builder.Services.Configure<LifeStorageOptions>(
    builder.Configuration.GetSection(LifeStorageOptions.SectionName));

// Register application + infrastructure services (compute engine, storage, metrics).
builder.Services.AddLifeApplication(builder.Configuration);

builder.Services.AddHealthChecks();

var app = builder.Build();

// Ensure the relational schema exists when the SQLite provider is selected (no-op otherwise).
await app.Services.InitializeLifeStorageAsync();

// Global exception handling first so it wraps everything downstream.
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health");
app.MapLifeEndpoints();

app.Run();

// Exposed so the integration test project can reference the entry point via WebApplicationFactory.
public partial class Program;
