using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Swashbuckle.AspNetCore.Annotations;
using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.Extensions.Options;
using System.Net.NetworkInformation;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.AspNetCore.Mvc;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;

var builder = WebApplication.CreateBuilder(args);

// builder.Services.AddApplicationInsightsTelemetry((ApplicationInsightsServiceOptions configuration) =>
// {
//     Console.WriteLine($"AddAutoCollectedMetricExtractor {configuration.AddAutoCollectedMetricExtractor}");
//     Console.WriteLine($"ApplicationVersion {configuration.ApplicationVersion}");
//     Console.WriteLine($"ConnectionString {configuration.ConnectionString}");
//     Console.WriteLine($"DependencyCollectionOptions.EnableLegacyCorrelationHeadersInjection {configuration.DependencyCollectionOptions.EnableLegacyCorrelationHeadersInjection}");
//     Console.WriteLine($"DeveloperMode {configuration.DeveloperMode}");
//     Console.WriteLine($"EnableActiveTelemetryConfigurationSetup {configuration.EnableActiveTelemetryConfigurationSetup}");
//     Console.WriteLine($"EnableAdaptiveSampling {configuration.EnableAdaptiveSampling}");
//     Console.WriteLine($"EnableAppServicesHeartbeatTelemetryModule {configuration.EnableAppServicesHeartbeatTelemetryModule}");
//     Console.WriteLine($"EnableAuthenticationTrackingJavaScript {configuration.EnableAuthenticationTrackingJavaScript}");
//     Console.WriteLine($"EnableAzureInstanceMetadataTelemetryModule {configuration.EnableAzureInstanceMetadataTelemetryModule}");
//     Console.WriteLine($"EnableDebugLogger {configuration.EnableDebugLogger}");
//     Console.WriteLine($"EnableDependencyTrackingTelemetryModule {configuration.EnableDependencyTrackingTelemetryModule}");
//     Console.WriteLine($"EnableDiagnosticsTelemetryModule {configuration.EnableDiagnosticsTelemetryModule}");
//     Console.WriteLine($"EnableEventCounterCollectionModule {configuration.EnableEventCounterCollectionModule}");
//     Console.WriteLine($"EnableHeartbeat {configuration.EnableHeartbeat}");
//     Console.WriteLine($"EnablePerformanceCounterCollectionModule {configuration.EnablePerformanceCounterCollectionModule}");
//     Console.WriteLine($"EnableQuickPulseMetricStream {configuration.EnableQuickPulseMetricStream}");
//     Console.WriteLine($"EnableRequestTrackingTelemetryModule {configuration.EnableRequestTrackingTelemetryModule}");
//     Console.WriteLine($"EndpointAddress {configuration.EndpointAddress}");
//     Console.WriteLine($"InstrumentationKey {configuration.InstrumentationKey}");
//     Console.WriteLine($"RequestCollectionOptions.EnableW3CDistributedTracing {configuration.RequestCollectionOptions.EnableW3CDistributedTracing}");
//     Console.WriteLine($"RequestCollectionOptions.InjectResponseHeaders {configuration.RequestCollectionOptions.InjectResponseHeaders}");
//     Console.WriteLine($"RequestCollectionOptions.TrackExceptions {configuration.RequestCollectionOptions.TrackExceptions}");
// });

//https://learn.microsoft.com/en-us/azure/azure-monitor/app/sampling-classic-api#configure-sampling-settings
// builder.Services.Configure<TelemetryConfiguration>(telemetryConfiguration =>
// {
//     var telemetryProcessorChainBuilder = telemetryConfiguration.DefaultTelemetrySink.TelemetryProcessorChainBuilder;
//
//     telemetryProcessorChainBuilder.UseAdaptiveSampling(excludedTypes: "Dependency;Exception");
//
//     telemetryProcessorChainBuilder.Build();
// });
//
// builder.Services.AddApplicationInsightsTelemetry(new ApplicationInsightsServiceOptions
// {
//     EnableAdaptiveSampling = false,
// });

builder.Services.AddApplicationInsightsTelemetry();

builder.Services.ConfigureTelemetryModule<DependencyTrackingTelemetryModule>((DependencyTrackingTelemetryModule configModule, ApplicationInsightsServiceOptions serviceOptions) =>
{
    configModule.EnableSqlCommandTextInstrumentation = true;
});

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


app.MapGet("/status",
        (int? statusCode, ILogger<IApiMarker> logger, IConfiguration configuration, TelemetryClient telemetryClient,
            IOptions<ApplicationInsightsServiceOptions> options) =>
        {
            var applicationInsightsLogLevelDefault = configuration["Logging:ApplicationInsights:LogLevel:Default"];

            statusCode ??= StatusCodes.Status200OK;

            telemetryClient.TrackEvent("StatusRequested", new Dictionary<string, string>
            {
                { "StatusCode", statusCode.ToString()! }
            });

            logger.LogTrace("Returning status code {StatusCode} with trace, {applicationInsightsLogLevelDefault}",
                statusCode, applicationInsightsLogLevelDefault);
            logger.LogDebug("Returning status code {StatusCode} with debug, {applicationInsightsLogLevelDefault}",
                statusCode, applicationInsightsLogLevelDefault);
            logger.LogInformation(
                "Returning status code {StatusCode} with information, {applicationInsightsLogLevelDefault}", statusCode,
                applicationInsightsLogLevelDefault);
            logger.LogWarning("Returning status code {StatusCode} with warning, {applicationInsightsLogLevelDefault}",
                statusCode, applicationInsightsLogLevelDefault);
            logger.LogError("Returning status code {StatusCode} with error, {applicationInsightsLogLevelDefault}",
                statusCode, applicationInsightsLogLevelDefault);
            logger.LogCritical("Returning status code {StatusCode} with critical, {applicationInsightsLogLevelDefault}",
                statusCode, applicationInsightsLogLevelDefault);
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

//using (app.Logger.BeginScope("panicoenlaxbox"))
using (app.Logger.BeginScope(new Dictionary<string, object>
       {
           { "Nick", "panicoenlaxbox" },
           { "Age", 48 }
       }))
{
    app.Logger.LogTrace("Trace");
    app.Logger.LogDebug("Debug");
    app.Logger.LogInformation("Information");
    app.Logger.LogWarning("Warning");
    app.Logger.LogError("Error");
    app.Logger.LogCritical("Critical");
}


// var path = Path.Combine(Directory.GetCurrentDirectory(), $"{Guid.NewGuid()}.txt");
// await File.WriteAllTextAsync(path, app.Configuration["Logging:ApplicationInsights:LogLevel:Default"]);

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
        if (telemetry is ISupportProperties itemProperties)
        {
            itemProperties.Properties.TryAdd("customProp", "customValue");
        }
    }
}