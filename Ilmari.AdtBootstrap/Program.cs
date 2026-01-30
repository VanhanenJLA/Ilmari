using Azure;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using Newtonsoft.Json.Linq;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing env var: {name}");

// var adtUrl = RequireEnv("ADT_SERVICE_URL"); // e.g. https://dt-ilmari-dev.api.neu.digitaltwins.azure.net
var adtUrl = "https://dt-ilmari-dev.api.sno.digitaltwins.azure.net"; // TODO: Hard coded.
var env = Environment.GetEnvironmentVariable("ILMARI_ENV") ?? "dev";

var client = new DigitalTwinsClient(new Uri(adtUrl), new DefaultAzureCredential());
Console.WriteLine($"ADT: {adtUrl}");
Console.WriteLine("Authenticating with DefaultAzureCredential...");

async Task UpsertModelAsync(string json)
{
    try
    {
        await client.CreateModelsAsync(new[] { json });
        Console.WriteLine("Model created.");
    }
    catch (RequestFailedException ex) when (ex.Status == 409) // CreateModelsAsync throws if model already exists; treat that as OK.
    {
        Console.WriteLine("Model already exists (409) - skipping.");
    }
}

async Task UpsertTwinAsync(string twinId, string modelId, IDictionary<string, object?> props)
{
    var twin = new BasicDigitalTwin
    {
        Id = twinId,
        Metadata = { ModelId = modelId }
    };

    foreach (var kv in props)
    {
        if (kv.Value is null)
        {
            twin.Contents[kv.Key] = null!;
            continue;
        }
        twin.Contents[kv.Key] = kv.Value;
    }

    try
    {
        await client.CreateOrReplaceDigitalTwinAsync(twinId, twin);
        Console.WriteLine($"Twin upserted: {twinId}");
    }
    catch (RequestFailedException ex)
    {
        Console.WriteLine($"Failed twin {twinId}: {ex.Status} {ex.ErrorCode} {ex.Message}");
        throw;
    }
}

async Task UpsertRelAsync(string srcId, string relId, string relName, string targetId)
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
        Console.WriteLine($"Rel upserted: {srcId} -[{relName}]-> {targetId}");
    }
    catch (RequestFailedException ex)
    {
        Console.WriteLine($"Failed rel {relId}: {ex.Status} {ex.Message}");
        throw;
    }
}

// 1) Upload models
Console.WriteLine("Uploading models...");
var modelFiles = new[]
{
    "Models/Sensor.json",
    "Models/HvacUnit.json",
    "Models/Room.json",
    "Models/Floor.json",
    "Models/Building.json"
};

foreach (var f in modelFiles)
{
    var json = await File.ReadAllTextAsync(f);
    await UpsertModelAsync(json);
}

// Model IDs
const string M_Building = "dtmi:ilmari:building:Building;1";
const string M_Floor    = "dtmi:ilmari:building:Floor;1";
const string M_Room     = "dtmi:ilmari:building:Room;1";
const string M_Hvac     = "dtmi:ilmari:building:HvacUnit;1";
const string M_Sensor   = "dtmi:ilmari:building:Sensor;1";

// 2) Create a small graph
var buildingId = $"bldg-ilmari-{env}";
var floorId = $"{buildingId}-floor-01";

Console.WriteLine("Creating twins...");
await UpsertTwinAsync(buildingId, M_Building, new Dictionary<string, object?>
{
    ["name"] = "Ilmari Demo Building",
    ["site"] = "North Europe"
});

await UpsertTwinAsync(floorId, M_Floor, new Dictionary<string, object?>
{
    ["floorNumber"] = 1
});

// Building contains Floor
await UpsertRelAsync(buildingId, $"{buildingId}-contains-floor-01", "contains", floorId);

// Rooms + HVAC + sensors
for (int roomNo = 101; roomNo <= 105; roomNo++)
{
    var roomId = $"{buildingId}-room-{roomNo}";
    var hvacId = $"{roomId}-hvac";

    await UpsertTwinAsync(roomId, M_Room, new Dictionary<string, object?>
    {
        ["roomId"] = roomId,
        ["occupancy"] = false,
        ["occupancyConfidence"] = 0.0,
        ["tempC"] = 22.0,
        ["humidityPct"] = 40.0,
        ["co2Ppm"] = 500.0,
        ["energyKw"] = 0.2,
        ["lastUpdated"] = DateTimeOffset.UtcNow
    });

    await UpsertTwinAsync(hvacId, M_Hvac, new Dictionary<string, object?>
    {
        ["mode"] = "off",
        ["setpointC"] = 22.0,
        ["powerKw"] = 0.0,
        ["faultCode"] = ""
    });

    // Relationships: floor contains room, room servedBy hvac
    await UpsertRelAsync(floorId, $"{floorId}-contains-{roomNo}", "contains", roomId);
    await UpsertRelAsync(roomId, $"{roomId}-servedBy", "servedBy", hvacId);

    // Sensors
    var sensors = new[]
    {
        new { Id = $"{roomId}-sensor-temp", Type = "temperature", Unit = "C" },
        new { Id = $"{roomId}-sensor-co2",  Type = "co2",        Unit = "ppm" },
        new { Id = $"{roomId}-sensor-kw",   Type = "power",      Unit = "kW" }
    };

    foreach (var s in sensors)
    {
        await UpsertTwinAsync(s.Id, M_Sensor, new Dictionary<string, object?>
        {
            ["sensorType"] = s.Type,
            ["unit"] = s.Unit,
            ["lastValue"] = 0.0,
            ["lastTimestamp"] = DateTimeOffset.UtcNow,
            ["status"] = "ok"
        });

        await UpsertRelAsync(roomId, $"{roomId}-hasSensor-{s.Type}", "hasSensor", s.Id);
    }
}

Console.WriteLine("✅ ADT bootstrap complete.");
Console.WriteLine("Try queries in ADT Explorer, e.g.:");
Console.WriteLine($"SELECT * FROM digitaltwins WHERE IS_OF_MODEL('{M_Room}')");
