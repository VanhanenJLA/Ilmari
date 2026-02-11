using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Ilmari.Functions;

public class ProcessAlertRules
{
    private const string RoomModelId = "dtmi:ilmari:building:Room;1";
    private const string AlertTypeTemp = "TempOutOfRange";
    private const int AlertSeverityTemp = 2;

    private readonly ILogger _log;
    private readonly DigitalTwinsClient _dt;
    private readonly ServiceBusSender _sender;
    
    private const double TempMinC = 18;
    private const double TempMaxC = 24;

    public ProcessAlertRules(ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<ProcessAlertRules>();

        var adtUrl = GetRequired("ADT_SERVICE_URL");
        _dt = new DigitalTwinsClient(new Uri(adtUrl), new DefaultAzureCredential());

        var sbConn = GetRequired("ALERTS_SERVICEBUS_CONNECTION");
        var topicName = GetRequired("ALERTS_TOPIC_NAME");
        var sbClient = new ServiceBusClient(sbConn);
        _sender = sbClient.CreateSender(topicName);
    }

    [Function("ProcessAlertRules")]
    public async Task Run(
        [TimerTrigger("0/30 * * * * *")]
        TimerInfo timer, 
        FunctionContext context)
    {
        _log.LogInformation("Info");
        _log.LogWarning("Warning");
        _log.LogError("Error");
        
        var ct = context.CancellationToken;
        var query =
            $"SELECT $dtId, roomId, tempC " +
            $"FROM digitaltwins " +
            $"WHERE IS_OF_MODEL('{RoomModelId}') " +
            $"AND IS_DEFINED(roomId) " +
            $"AND (tempC < {TempMinC} " + $"OR tempC > {TempMaxC})";

        var count = 0;
        var rooms = _dt.QueryAsync<RoomProjection>(query, cancellationToken: ct);
        
        await foreach (var room in rooms)
        {
            if (string.IsNullOrWhiteSpace(room.RoomId))
            {
                _log.LogWarning("Skipping room twin {TwinId} because roomId is missing", room.Id);
                continue;
            }

            var roomId = room.RoomId;
            var tempC = room.TempC;
            var breach = tempC < TempMinC ? "Low" : "High";

            var alert = new AlertMessage(
                AlertTypeTemp,
                AlertSeverityTemp,
                room.Id,
                roomId,
                DateTimeOffset.UtcNow,
                tempC,
                TempMinC,
                TempMaxC,
                breach
            );

            var body = JsonSerializer.Serialize(alert);
            var message = new ServiceBusMessage(body)
            {
                ApplicationProperties =
                {
                    ["AlertType"] = alert.AlertType,
                    ["Severity"] = alert.Severity,
                    ["RoomId"] = alert.RoomId,
                    ["TwinId"] = alert.TwinId
                }
            };

            await _sender.SendMessageAsync(message, ct);
            count++;
        }

        _log.LogInformation(
            "ProcessAlertRules evaluated temp range {MinTempC}-{MaxTempC}C and emitted {Count} alerts",
            TempMinC,
            TempMaxC,
            count);
    }

    private static string GetRequired(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing {name}");


    private record AlertMessage(
        string AlertType,
        int Severity,
        string TwinId,
        string RoomId,
        DateTimeOffset ObservedAt,
        double TempC,
        double MinTempC,
        double MaxTempC,
        string Breach
    );

    private sealed class RoomProjection
    {
        [JsonPropertyName("$dtId")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("roomId")]
        public string? RoomId { get; set; }

        [JsonPropertyName("tempC")]
        public double TempC { get; set; }
    }
}
