using System.Text;
using System.Text.Json;
using Microsoft.Azure.Devices.Client;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .Build();

var connStr = configuration["IOTHUB_DEVICE_CONNECTION_STRING"]
              ?? Environment.GetEnvironmentVariable("IOTHUB_DEVICE_CONNECTION_STRING")
              ?? throw new InvalidOperationException("Missing IOTHUB_DEVICE_CONNECTION_STRING in User Secrets or env vars.");

var env = EnvStr("ILMARI_ENV", "dev");
var roomCount = EnvInt("SIM_ROOMS", 2);
var intervalMs = EnvInt("SIM_INTERVAL_MS", 5000);

var deviceClient = DeviceClient.CreateFromConnectionString(
    connStr,
    TransportType.Mqtt_Tcp_Only
);

deviceClient.OperationTimeoutInMilliseconds = 30000;

Console.WriteLine("Ilmari Simulator starting...");
Console.WriteLine($"Env={env}, Rooms={roomCount}, IntervalMs={intervalMs}");
Console.WriteLine("Sending telemetry to IoT Hub as one device (multi-room payloads).");

// -------- Simulation model --------

var rng = new Random(42);
var buildingId = $"building-ilmari-{env}";
var rooms = Enumerable.Range(0, roomCount)
    .Select(i => new RoomState(
        $"{buildingId}-room-{101 + i}",
        22.0 + rng.NextDouble(),
        38 + rng.NextDouble() * 6,
        450 + rng.NextDouble() * 80,
        false,
        0.2,
        0.0,
        "off",
        22.0
    ))
    .ToList();

// Scenario timeline (repeatable)
// - At t=1min: Room 101 becomes occupied
// - At t=2min: Room 102 CO2 starts rising
// - At t=3min: Room 101 becomes unoccupied
// - At t=4min: Room 101 temp and Room 102 CO2 return to normal
// - At t=5min: Loop scenario
var simStart = DateTimeOffset.UtcNow;

while (true)
{
    var now = DateTimeOffset.UtcNow;
    var elapsed = now - simStart;

    ApplyScenario(elapsed, rooms);

    foreach (var room in rooms)
    {
        StepRoom(now, room, rng);

        var payload = new Telemetry(
            room.RoomId,
            now,
            Math.Round(room.TempC, 2),
            Math.Round(room.HumidityPct, 1),
            Math.Round(room.Co2Ppm, 0),
            room.Occupancy,
            Math.Round(room.EnergyKw, 2),
            Math.Round(room.HvacPowerKw, 2),
            room.HvacMode,
            Math.Round(room.SetpointC, 1)
        );

        var json = JsonSerializer.Serialize(payload);
        var msg = new Message(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8"
        };

        // Useful message metadata for routing/debugging
        msg.Properties["roomId"] = room.RoomId;
        msg.Properties["env"] = env;
        msg.Properties["schema"] = "ilmari.telemetry.v1";

        await deviceClient.SendEventAsync(msg);
        Console.WriteLine(
            $"{now:HH:mm:ss} sent {room.RoomId} occ={room.Occupancy} temp={payload.TempC} co2={payload.Co2Ppm} kW={payload.EnergyKw} hvac={payload.HvacMode}/{payload.HvacPowerKw}");
    }

    await Task.Delay(intervalMs);
}

static string EnvStr(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) 
    ?? fallback;

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) 
        ? v 
        : fallback;

static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name) 
    ?? throw new InvalidOperationException($"Missing env var: {name}");

// -------- Types & functions --------
static void ApplyScenario(TimeSpan elapsed, List<RoomState> rooms)
{
    // Reset defaults each tick
    foreach (var r in rooms)
    {
        r.ComfortDriftHot = false;
        r.Co2DriftHigh = false;
    }

    // Repeat every 5 minutes
    var t = elapsed.TotalMinutes % 5.0;

    var r101 = rooms[0];
    var r102 = rooms[1];

    // Room 101 occupied from 1..3 min
    if (t is >= 1 and < 3) 
        r101.Occupancy = true;
    else
        r101.Occupancy = false;
    
    // Room 101: comfort issue from 1.5..4 min (occupied, temp drifts hot)
    if (t is >= 2 and < 4)
    {
        Console.WriteLine("Simulated comfort issue in room 101.");
        r101.Occupancy = true;
        r101.ComfortDriftHot = true;
        r101.HvacMode = "off";
        r101.SetpointC = 25.0;
    }

    // Room 102: CO2 issue from 2..4 min
    if (t is >= 2 and < 4)
    {
        Console.WriteLine("Simulated CO2 issue in room 102.");
        r102.Occupancy = true;
        r102.Co2DriftHigh = true;
        r102.HvacMode = "eco";
        r102.SetpointC = 22.0;
    }

}

static void StepRoom(DateTimeOffset now, RoomState r, Random rng)
{
    // Basic ambient drift
    var ambient = 21;
    var tempNoise = (rng.NextDouble() - 0.5) * 0.08;
    r.TempC += (ambient - r.TempC) * 0.02 + tempNoise;

    // Humidity random walk
    r.HumidityPct = Clamp(r.HumidityPct + (rng.NextDouble() - 0.5) * 0.4, 25, 65);

    // CO2 dynamics
    // occupied -> rises; unoccupied -> decays back toward ~450
    var co2Target = r.Occupancy ? 1100 : 450;
    var co2Rate = r.Occupancy ? 0.06 : 0.08;
    r.Co2Ppm += (co2Target - r.Co2Ppm) * co2Rate + (rng.NextDouble() - 0.5) * 8;
    r.Co2Ppm = Clamp(r.Co2Ppm, 380, 2200);

    // HVAC behavior & energy
    // Simple control unless scenario overrides
    if (!r.Co2DriftHigh && !r.ComfortDriftHot)
    {
        // If occupied, HVAC maintains setpoint a bit
        if (r.Occupancy)
        {
            r.HvacMode = "eco";
            r.SetpointC = 22.0;
            // small corrective power
            var error = r.SetpointC - r.TempC;
            r.HvacPowerKw = Clamp(Math.Abs(error) * 0.6, 0.1, 1.2);
            r.TempC += Clamp(error * 0.05, -0.08, 0.08);
        }
        else
        {
            // Unoccupied -> mostly off
            r.HvacMode = "off";
            r.HvacPowerKw = 0.0;
        }
    }

    if (r.ComfortDriftHot)
    {
        // Occupied but HVAC off; temperature drifts upward
        r.TempC += 0.20 + rng.NextDouble() * 0.08;
        r.HvacPowerKw = 0.0;
    }

    if (r.Co2DriftHigh)
    {
        // CO2 rises despite normal occupancy.
        r.Co2Ppm = Clamp(r.Co2Ppm + 18 + rng.NextDouble() * 8, 380, 2200);
        r.HvacPowerKw = 0.2 + rng.NextDouble() * 0.2;
    }

    // Whole-room energy (HVAC + plug loads)
    var plugLoad = r.Occupancy ? 0.35 : 0.12;
    r.EnergyKw = Clamp(r.HvacPowerKw + plugLoad + (rng.NextDouble() - 0.5) * 0.05, 0.05, 5.0);
}

static double Clamp(double v, double min, double max) =>
    Math.Min(max, Math.Max(min, v));

internal record Telemetry(
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

internal class RoomState
{
    public string RoomId { get; }
    public double TempC { get; set; }
    public double HumidityPct { get; set; }
    public double Co2Ppm { get; set; }
    public bool Occupancy { get; set; }

    public double EnergyKw { get; set; }
    public double HvacPowerKw { get; set; }
    public string HvacMode { get; set; }
    public double SetpointC { get; set; }

    // Scenario flags
    public bool ComfortDriftHot { get; set; }
    public bool Co2DriftHigh { get; set; }
    
    public RoomState(
        string roomId,
        double tempC,
        double humidityPct,
        double co2Ppm,
        bool occupancy,
        double energyKw,
        double hvacPowerKw,
        string hvacMode,
        double setpointC)
    {
        RoomId = roomId;
        TempC = tempC;
        HumidityPct = humidityPct;
        Co2Ppm = co2Ppm;
        Occupancy = occupancy;
        EnergyKw = energyKw;
        HvacPowerKw = hvacPowerKw;
        HvacMode = hvacMode;
        SetpointC = setpointC;
    }
}
