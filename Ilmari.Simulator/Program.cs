using System.Text;
using System.Text.Json;
using Microsoft.Azure.Devices.Client;

static string RequireEnv(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing env var: {name}");

static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

static string EnvStr(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

var connStr = RequireEnv("IOTHUB_DEVICE_CONNECTION_STRING");
var env = EnvStr("ILMARI_ENV", "dev");
var roomCount = EnvInt("SIM_ROOMS", 5);
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
var buildingId = $"bldg-ilmari-{env}";
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
// - At t=2min: Room 101 becomes occupied (CO2 rises)
// - At t=4min: Room 102 waste: unoccupied but HVAC stuck on high
// - At t=6min: Room 103 comfort violation: temp drifts high while occupied
// - At t=8min: clear scenarios / return to normal, repeat
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
            $"{now:HH:mm:ss} sent {room.RoomId} occ={room.Occupancy} temp={payload.tempC} co2={payload.co2Ppm} kW={payload.energyKw} hvac={payload.hvacMode}/{payload.hvacPowerKw}");
    }

    await Task.Delay(intervalMs);
}

// -------- Types & functions --------

static void ApplyScenario(TimeSpan elapsed, List<RoomState> rooms)
{
    // Reset defaults each tick
    foreach (var r in rooms)
    {
        r.WasteStuckHvac = false;
        r.ComfortDriftHot = false;
    }

    // Repeat every 10 minutes
    var t = elapsed.TotalMinutes % 10.0;

    // Room 101 occupied from 2..8 min
    if (t >= 2 && t < 8 && rooms.Count >= 1)
        rooms[0].Occupancy = true;
    else if (rooms.Count >= 1)
        rooms[0].Occupancy = false;

    // Room 102: waste from 4..8 min (unoccupied but HVAC stuck high)
    if (t >= 4 && t < 8 && rooms.Count >= 2)
    {
        rooms[1].Occupancy = false;
        rooms[1].WasteStuckHvac = true;
        rooms[1].HvacMode = "cool";
        rooms[1].SetpointC = 21.0;
    }

    // Room 103: comfort issue from 6..8 min (occupied, temp drifts hot)
    if (t >= 6 && t < 8 && rooms.Count >= 3)
    {
        rooms[2].Occupancy = true;
        rooms[2].ComfortDriftHot = true;
        rooms[2].HvacMode = "off";
        rooms[2].SetpointC = 22.0;
    }
}

static void StepRoom(DateTimeOffset now, RoomState r, Random rng)
{
    // Basic ambient drift
    var ambient = 21.5;
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
    if (!r.WasteStuckHvac && !r.ComfortDriftHot)
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

    if (r.WasteStuckHvac)
    {
        // HVAC burns energy even though room unoccupied; temp changes slightly
        r.HvacPowerKw = 2.2 + rng.NextDouble() * 0.4;
        r.TempC += (r.SetpointC - r.TempC) * 0.03;
    }

    if (r.ComfortDriftHot)
    {
        // Occupied but HVAC off; temperature drifts upward
        r.TempC += 0.05 + rng.NextDouble() * 0.03;
        r.HvacPowerKw = 0.0;
    }

    // Whole-room energy (HVAC + plug loads)
    var plugLoad = r.Occupancy ? 0.35 : 0.12;
    r.EnergyKw = Clamp(r.HvacPowerKw + plugLoad + (rng.NextDouble() - 0.5) * 0.05, 0.05, 5.0);
}

static double Clamp(double v, double min, double max)
{
    return Math.Min(max, Math.Max(min, v));
}

internal record Telemetry(
    string roomId,
    DateTimeOffset ts,
    double tempC,
    double humidityPct,
    double co2Ppm,
    bool occupancy,
    double energyKw,
    double hvacPowerKw,
    string hvacMode,
    double setpointC
);

internal class RoomState
{
    public RoomState(string RoomId, double TempC, double HumidityPct, double Co2Ppm, bool Occupancy,
        double EnergyKw, double HvacPowerKw, string HvacMode, double SetpointC)
    {
        this.RoomId = RoomId;
        this.TempC = TempC;
        this.HumidityPct = HumidityPct;
        this.Co2Ppm = Co2Ppm;
        this.Occupancy = Occupancy;
        this.EnergyKw = EnergyKw;
        this.HvacPowerKw = HvacPowerKw;
        this.HvacMode = HvacMode;
        this.SetpointC = SetpointC;
    }

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
    public bool WasteStuckHvac { get; set; }
    public bool ComfortDriftHot { get; set; }
}