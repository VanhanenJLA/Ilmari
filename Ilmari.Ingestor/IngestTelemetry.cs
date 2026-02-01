using System.Text;
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

        // In Azure: Managed Identity (your Bicep already granted DT Data Owner to the Function MI)
        // Locally: uses Azure CLI / VS / etc.
        _dt = new DigitalTwinsClient(new Uri(adtUrl), new DefaultAzureCredential());
    }

    [Function("IngestTelemetry")]
    public async Task Run(
        [EventHubTrigger("%IOTHUB_EVENTHUB_PATH%", Connection = "IOTHUB_EVENTHUB_CONNECTION")]
        byte[] body,
        FunctionContext context)
    {
        var ct = context.CancellationToken;

        Telemetry? t;
        try
        {
            var json = Encoding.UTF8.GetString(body);
            t = JsonSerializer.Deserialize<Telemetry>(json, JsonOpts);

            if (t is null || string.IsNullOrWhiteSpace(t.RoomId))
            {
                _log.LogWarning("Telemetry missing RoomId. Payload: {Payload}", json);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to parse telemetry.");
            return;
        }

        // Patch Room twin (IDs match your bootstrapper: bldg-ilmari-dev-room-101 etc.)
        var patch = new JsonPatchDocument();
        patch.AppendReplace("/tempC", t.TempC);
        patch.AppendReplace("/humidityPct", t.HumidityPct);
        patch.AppendReplace("/co2Ppm", t.Co2Ppm);
        patch.AppendReplace("/occupancy", t.Occupancy);
        patch.AppendReplace("/energyKw", t.EnergyKw);
        patch.AppendReplace("/lastUpdated", t.Ts);

        try
        {
            await _dt.UpdateDigitalTwinAsync(t.RoomId, patch, cancellationToken: ct);
            _log.LogInformation("Updated Room {RoomId}: occ={Occ} temp={Temp} co2={Co2} kW={Kw}",
                t.RoomId, t.Occupancy, t.TempC, t.Co2Ppm, t.EnergyKw);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogError("Room twin not found: {RoomId}. Did you bootstrap ADT in the same env?", t.RoomId);
        }

        // OPTIONAL: patch HVAC twin too (your bootstrapper used "<roomId>-hvac")
        var hvacTwinId = $"{t.RoomId}-hvac";
        var hvacPatch = new JsonPatchDocument();
        hvacPatch.AppendReplace("/mode", t.HvacMode);
        hvacPatch.AppendReplace("/setpointC", t.SetpointC);
        hvacPatch.AppendReplace("/powerKw", t.HvacPowerKw);

        try
        {
            await _dt.UpdateDigitalTwinAsync(hvacTwinId, hvacPatch, cancellationToken: ct);
            _log.LogInformation("Updated HVAC {HvacId}: mode={Mode} set={Set} kW={Kw}",
                hvacTwinId, t.HvacMode, t.SetpointC, t.HvacPowerKw);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Not fatal
            _log.LogWarning("HVAC twin not found: {HvacId} (skipping).", hvacTwinId);
        }
    }

    // Matches your simulator JSON
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
