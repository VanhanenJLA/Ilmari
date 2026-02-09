using System.Text.Json;
using Azure;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using static System.Environment;

// var adtUrl = RequireEnv("ADT_SERVICE_URL"); // e.g. https://dt-ilmari-dev.api.neu.digitaltwins.azure.net
const string adtUrl = "https://dt-ilmari-dev.api.weu.digitaltwins.azure.net"; // TODO: Hard coded.
var env = GetEnvironmentVariable("ILMARI_ENV") ?? "dev";

var client = new DigitalTwinsClient(new Uri(adtUrl), new DefaultAzureCredential());
Console.WriteLine($"Connecting to ADT at: {adtUrl}");
Console.WriteLine("Authenticating with DefaultAzureCredential...");

Console.WriteLine();
Console.WriteLine("Uploading models...");
var modelDir = Path.Combine(AppContext.BaseDirectory, "Models");
var modelFiles = new[]
{
    Path.Combine(modelDir, "Sensor.json"),
    Path.Combine(modelDir, "HvacUnit.json"),
    Path.Combine(modelDir, "Room.json"),
    Path.Combine(modelDir, "Floor.json"),
    Path.Combine(modelDir, "Building.json")
};

foreach (var f in modelFiles)
{
    var json = await File.ReadAllTextAsync(f);
    await UpsertModelAsync(json);
}

const string M_Building = "dtmi:ilmari:building:Building;1";
const string M_Floor    = "dtmi:ilmari:building:Floor;1";
const string M_Room     = "dtmi:ilmari:building:Room;1";
const string M_Hvac     = "dtmi:ilmari:building:HvacUnit;1";
const string M_Sensor   = "dtmi:ilmari:building:Sensor;1";

var buildingId = $"building-ilmari-{env}";
var floorId = $"{buildingId}-floor-01";

Console.WriteLine();
Console.WriteLine("Creating a building with a floor...");

await UpsertTwinAsync(buildingId, M_Building, new Dictionary<string, object?>
{
    ["name"] = "Ilmari Demo Building",
    ["site"] = "North Europe"
});

await UpsertTwinAsync(floorId, M_Floor, new Dictionary<string, object?>
{
    ["floorNumber"] = 1
});

await UpsertRelationAsync(buildingId, $"{buildingId}-contains-floor-01", "contains", floorId);

Console.WriteLine();
Console.WriteLine("Populating rooms...");
Console.WriteLine();
for (var roomNumber = 101; roomNumber <= 102; roomNumber++)
{
    var roomId = $"{buildingId}-room-{roomNumber}";
    Console.WriteLine($"Populating room: {roomId}");
    var hvacId = $"{roomId}-hvac";
    
    var room = new Dictionary<string, object?>
    {
        ["roomId"] = roomId,
        ["occupancy"] = false,
        ["occupancyConfidence"] = 0.0,
        ["tempC"] = 22.0,
        ["humidityPct"] = 40.0,
        ["co2Ppm"] = 500.0,
        ["energyKw"] = 0.2,
        ["lastUpdated"] = DateTimeOffset.UtcNow
    };
    
    var hvac = new Dictionary<string, object?>
    {
        ["mode"] = "off",
        ["setpointC"] = 22.0,
        ["powerKw"] = 0.0,
        ["faultCode"] = ""
    };
    
    var sensors = new[]
    {
        new { Id = $"{roomId}-sensor-temp", Type = "temperature", Unit = "C" },
        new { Id = $"{roomId}-sensor-co2", Type = "co2", Unit = "ppm" },
        new { Id = $"{roomId}-sensor-kw", Type = "power", Unit = "kW" }
    };
    
    await UpsertTwinAsync(roomId, M_Room, room);
    await UpsertTwinAsync(hvacId, M_Hvac, hvac);
    
    await UpsertRelationAsync(floorId, $"{floorId}-contains-{roomNumber}", "contains", roomId);
    await UpsertRelationAsync(roomId, $"{roomId}-servedBy", "servedBy", hvacId);

    Console.WriteLine();
    Console.WriteLine($"Populating sensors of room: {roomId}");
    foreach (var s in sensors)
    {
        var sensor = new Dictionary<string, object?>
        {
            ["sensorType"] = s.Type,
            ["unit"] = s.Unit,
            ["lastValue"] = 0.0,
            ["lastTimestamp"] = DateTimeOffset.UtcNow,
            ["status"] = "ok"
        };
        await UpsertTwinAsync(s.Id, M_Sensor, sensor);
        await UpsertRelationAsync(roomId, $"{roomId}-hasSensor-{s.Type}", "hasSensor", s.Id);
    }
    Console.WriteLine();
}

Console.WriteLine("✅ Azure Data Twin bootstrap finished.");
Console.WriteLine("Try querying the ADT Explorer, e.g.:");
Console.WriteLine($"SELECT * FROM digitaltwins WHERE IS_OF_MODEL('{M_Room}')");
return;

static string RequireEnv(string name) =>
    GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing env var: {name}");

async Task UpsertTwinAsync(string twinId, string modelId, IDictionary<string, object?> props)
{
    var twin = new BasicDigitalTwin
    {
        Id = twinId,
        Metadata = { ModelId = modelId }
    };

    foreach (var (key, value) in props)
    {
        if (value is null)
        {
            twin.Contents[key] = null!;
            continue;
        }
        twin.Contents[key] = value;
    }

    try
    {
        await client.CreateOrReplaceDigitalTwinAsync(twinId, twin);
        Console.WriteLine($"Twin updated: '{twinId}'");
    }
    catch (RequestFailedException ex)
    {
        Console.WriteLine($"Failed twin update: {twinId} {ex.Status} {ex.ErrorCode} {ex.Message}");
        throw;
    }
}

async Task UpsertRelationAsync(string srcId, string relId, string relName, string targetId)
{
    var rel = new BasicRelationship
    {
        Id = relId,
        SourceId = srcId,
        TargetId = targetId,
        Name = relName
    };

    try
    {
        await client.CreateOrReplaceRelationshipAsync(srcId, relId, rel);
        Console.WriteLine($"Relation updated: {srcId} -[{relName}]-> {targetId}");
    }
    catch (RequestFailedException ex)
    {
        Console.WriteLine($"Failed updating relation '{relId}' due to: {ex.Status} {ex.Message}");
        throw;
    }
}

async Task UpsertModelAsync(string json)
{
    var modelId = TryGetModelId(json) ?? "<unknown model>";
    try
    {
        await client.CreateModelsAsync(new[] { json });
        Console.WriteLine($"Model created: {modelId}");
    }
    catch (RequestFailedException ex) when (ex.Status == 409) // CreateModelsAsync throws if model already exists; treat that as OK.
    {
        Console.WriteLine($"Model already exists (409) - skipping: {modelId}");
    }
}

static string? TryGetModelId(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("@id", out var idProp))
        {
            return idProp.GetString();
        }
    }
    catch (JsonException)
    {
        // Ignore parse failures; caller will print a fallback.
    }

    return null;
}
