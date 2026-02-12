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
    private static readonly RuleDefinition[] Rules =
    {
        new("TempOutOfRange", "tempC", "C", 2, 18, 24, room => room.TempC),
        new("Co2OutOfRange", "co2Ppm", "ppm", 2, null, 1000, room => room.Co2Ppm)
    };

    private readonly ILogger _log;
    private readonly DigitalTwinsClient _dt;
    private readonly ServiceBusSender _sender;

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
        [TimerTrigger("0/10 * * * * *")]
        TimerInfo timer, 
        FunctionContext context)
    {
        
        var ct = context.CancellationToken;
        var query =
            $"SELECT $dtId, roomId, tempC, co2Ppm " +
            $"FROM digitaltwins " +
            $"WHERE IS_OF_MODEL('{RoomModelId}') " +
            $"AND IS_DEFINED(roomId)";

        var alertCount = 0;
        var rooms = _dt.QueryAsync<RoomProjection>(query, cancellationToken: ct);
        
        await foreach (var room in rooms)
        {
            if (string.IsNullOrWhiteSpace(room.RoomId))
            {
                _log.LogWarning("Skipping room twin {TwinId} because roomId is missing", room.Id);
                continue;
            }

            var roomId = room.RoomId;
            foreach (var rule in Rules)
            {
                var observedValue = rule.SelectValue(room);
                var breach = rule.GetBreach(observedValue);
                
                if (breach is null) continue;

                var alert = new AlertMessage(
                    rule.AlertType,
                    rule.Severity,
                    room.Id,
                    roomId,
                    DateTimeOffset.UtcNow,
                    rule.Metric,
                    rule.Unit,
                    observedValue,
                    rule.MinValue,
                    rule.MaxValue,
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
                        ["TwinId"] = alert.TwinId,
                        ["Metric"] = alert.Metric
                    }
                };

                await _sender.SendMessageAsync(message, ct);
                alertCount++;
            }
        }

        _log.LogInformation(
            "ProcessAlertRules evaluated {RuleCount} rules and emitted {AlertCount} alerts",
            Rules.Length,
            alertCount);
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
        string Metric,
        string Unit,
        double ObservedValue,
        double? MinValue,
        double? MaxValue,
        string Breach
    );

    private sealed class RuleDefinition(
        string alertType,
        string metric,
        string unit,
        int severity,
        double? minValue,
        double? maxValue,
        Func<RoomProjection, double> selectValue)
    {
        public string AlertType { get; } = alertType;
        public string Metric { get; } = metric;
        public string Unit { get; } = unit;
        public int Severity { get; } = severity;
        public double? MinValue { get; } = minValue;
        public double? MaxValue { get; } = maxValue;
        public Func<RoomProjection, double> SelectValue { get; } = selectValue;

        public string? GetBreach(double observedValue)
        {
            if (observedValue < MinValue)
            {
                return "Low";
            }

            if (observedValue > MaxValue)
            {
                return "High";
            }

            return null;
        }
    }

    private sealed class RoomProjection
    {
        [JsonPropertyName("$dtId")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("roomId")]
        public string? RoomId { get; set; }
        [JsonPropertyName("tempC")]
        public double TempC { get; set; }
        [JsonPropertyName("co2Ppm")]
        public double Co2Ppm { get; set; }
    }
}
