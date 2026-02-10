using System.Text.Json;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ilmari.Ingestor;

public class IngestTelemetry
{
    private readonly ILogger _log;
    private readonly DigitalTwinsClient _dt;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IngestTelemetry(ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<IngestTelemetry>();

        var adtUrl = Environment.GetEnvironmentVariable("ADT_SERVICE_URL")
            ?? throw new InvalidOperationException("Missing ADT_SERVICE_URL");

        var cred = new DefaultAzureCredential();
        _dt = new DigitalTwinsClient(new Uri(adtUrl), cred);
    }

    [Function("IngestTelemetry")]
    public async Task Run(
        [EventHubTrigger("%IOTHUB_EVENTHUB_NAME%", Connection = "IOTHUB_EVENTHUB_CONNECTION")]
        string[] events,
        FunctionContext context)
    {
        var ct = context.CancellationToken;

        foreach (var json in events)
        {
            Telemetry? t;
            try
            {
                t = JsonSerializer.Deserialize<Telemetry>(json, JsonOpts);
                if (t is null || string.IsNullOrWhiteSpace(t.RoomId))
                {
                    _log.LogWarning("Telemetry missing RoomId. Payload: {Payload}", json);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse telemetry. Payload: {Payload}", json);
                continue;
            }

            await PatchAdtAsync(t, ct);
        }
    }

    private async Task PatchAdtAsync(Telemetry t, CancellationToken ct)
    {
        var patch = new JsonPatchDocument();
        patch.AppendReplace("/tempC", t.TempC);
        patch.AppendReplace("/humidityPct", t.HumidityPct);
        patch.AppendReplace("/co2Ppm", t.Co2Ppm);
        patch.AppendReplace("/occupancy", t.Occupancy);
        patch.AppendReplace("/energyKw", t.EnergyKw);
        patch.AppendReplace("/lastUpdated", t.Ts);

        await _dt.UpdateDigitalTwinAsync(t.RoomId, patch, cancellationToken: ct);

        var hvacTwinId = $"{t.RoomId}-hvac";
        var hvacPatch = new JsonPatchDocument();
        hvacPatch.AppendReplace("/mode", t.HvacMode);
        hvacPatch.AppendReplace("/setpointC", t.SetpointC);
        hvacPatch.AppendReplace("/powerKw", t.HvacPowerKw);

        // best-effort
        try { await _dt.UpdateDigitalTwinAsync(hvacTwinId, hvacPatch, cancellationToken: ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { }
    }
    
    public record Telemetry(
        string RoomId,
        DateTimeOffset Ts,
        double TempC,
        double HumidityPct,
        double Co2Ppm,
        bool Occupancy,
        double EnergyKw,
        double HvacPowerKw,
        string HvacMode,
        double SetpointC
    );
}
