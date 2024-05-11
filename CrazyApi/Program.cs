using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Swashbuckle.AspNetCore.Annotations;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddApplicationInsightsTelemetryProcessor<SuccessfulDependencyFilter>();
builder.Services.AddApplicationInsightsTelemetryProcessor<SyntheticRequestsDependencyFilter>();
builder.Services.AddApplicationInsightsTelemetryProcessor<FastRemoteDependencyCallsFilter>();

builder.Services.AddSingleton<ITelemetryInitializer, Convert400To200TelemetryInitializer>();
builder.Services.AddSingleton<ITelemetryInitializer, CustomPropertyTelemetryInitializer>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => { options.EnableAnnotations(); });

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();

app.MapGet("/status", (int? statusCode, ILogger<IApiMarker> logger, IConfiguration configuration) =>
    {
        var applicationInsightsLogLevelDefault = configuration["Logging:ApplicationInsights:LogLevel:Default"];
        logger.LogInformation("Logging:ApplicationInsights:LogLevel:Default = {ApplicationInsightsLogLevelDefault}",
            applicationInsightsLogLevelDefault);

        statusCode ??= StatusCodes.Status200OK;
        logger.LogTrace("Returning status code {StatusCode} with trace", statusCode);
        logger.LogDebug("Returning status code {StatusCode} with debug", statusCode);
        logger.LogInformation("Returning status code {StatusCode} with information", statusCode);
        logger.LogWarning("Returning status code {StatusCode} with warning", statusCode);
        logger.LogError("Returning status code {StatusCode} with error", statusCode);
        logger.LogCritical("Returning status code {StatusCode} with critical", statusCode);
        return Results.StatusCode((int)statusCode);
    })
    .WithName("GetStatus")
    .WithOpenApi();

app.MapPost("/payload",
        (Payload payload) => Results.Text(payload.Content, payload.ContentType, statusCode: payload.StatusCode))
    .WithName("PostPayload")
    .WithOpenApi();

app.MapGet("/fail", () => { throw new DivideByZeroException(); })
    .WithName("Fail")
    .WithOpenApi();

app.MapGet("/delay/{milliseconds:int}", async (int milliseconds, CancellationToken cancellationToken) =>
{
    await Task.Delay(milliseconds, cancellationToken);
    return Results.Ok();
});

app.MapGet("/cpu/{milliseconds:int}", (int milliseconds, CancellationToken cancellationToken) =>
    {
        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            var sw = new Stopwatch();
            sw.Start();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sw.ElapsedMilliseconds >= milliseconds)
                {
                    sw.Stop();
                    break;
                }
            }
        });
        return Results.Ok();
    })
    .WithName("Cpu")
    .WithOpenApi();

app.Logger.LogTrace("Trace");
app.Logger.LogDebug("Debug");
app.Logger.LogInformation("Information");
app.Logger.LogWarning("Warning");
app.Logger.LogError("Error");
app.Logger.LogCritical("Critical");

app.Run();

public class Payload
{
    public int StatusCode { get; set; }
    [SwaggerSchema(Nullable = false)] public string ContentType { get; set; }
    [SwaggerSchema(Nullable = false)] public string Content { get; set; }
}

public interface IApiMarker
{
}

public class SuccessfulDependencyFilter(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        // Microsoft.ApplicationInsights.DataContracts.TraceTelemetry
        // Microsoft.ApplicationInsights.DataContracts.RequestTelemetry
        // Microsoft.ApplicationInsights.DataContracts.DependencyTelemetry
        // Microsoft.ApplicationInsights.DataContracts.MetricTelemetry
        if (item is DependencyTelemetry { Success: true })
        {
            return;
        }

        next.Process(item);
    }
}

public class SyntheticRequestsDependencyFilter(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        if (!string.IsNullOrEmpty(item.Context.Operation.SyntheticSource))
        {
            return;
        }

        next.Process(item);
    }
}

public class FastRemoteDependencyCallsFilter(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        if (item is DependencyTelemetry telemetry && telemetry.Duration < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        next.Process(item);
    }
}

public class Convert400To200TelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        // Is this a TrackRequest() ?
        if (telemetry is not RequestTelemetry requestTelemetry)
        {
            return;
        }

        var parsed = int.TryParse(requestTelemetry.ResponseCode, out var code);
        if (!parsed)
        {
            return;
        }

        if (code is >= 400 and < 500)
        {
            // If we set the Success property, the SDK won't change it:
            requestTelemetry.Success = true;

            // Allow us to filter these requests in the portal:
            requestTelemetry.Properties["Overridden400s"] = "true";
        }
        // else leave the SDK to set the Success property
    }
}

public class CustomPropertyTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        if(telemetry is ISupportProperties itemProperties)
        {
            itemProperties.Properties.TryAdd("customProp", "customValue");
        }
    }
}