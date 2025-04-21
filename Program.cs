using OpampServerDemo.Handlers;
using OpampServerDemo.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers(); // Needed for basic Web API setup, even if not using controllers directly for OpAMP

// --- Register OpAMP Services ---
builder.Services.AddSingleton<IConfigurationService, ConfigurationService>(); // Singleton for demo config
builder.Services.AddTransient<OpampHandler>(); // Handler per request/connection

// --- Configure Logging (Optional: adjust as needed) ---
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();


var app = builder.Build();

// Configure the HTTP request pipeline.

// --- Enable WebSockets ---
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2) // Example keep-alive
};
app.UseWebSockets(webSocketOptions);
// ------------------------

// --- Map OpAMP WebSocket Endpoint ---
// Use a specific path consistent with OpAMP conventions (e.g., /v1/opamp)
app.Map("/v1/opamp", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        try
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = context.RequestServices.GetRequiredService<OpampHandler>();
            await handler.HandleWebSocketAsync(webSocket);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Error accepting or handling WebSocket connection.");
            // Optionally write error details to response if appropriate (careful with exposing info)
            // await context.Response.WriteAsync("WebSocket handling error.");
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Requires WebSocket connection.");
    }
});
// ---------------------------------

// Basic HTTP handling (optional, good for health check)
app.MapGet("/", () => "OpAMP Server Demo Running");

app.Run();