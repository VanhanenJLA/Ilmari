using System.Text.Json;
using Azure.DigitalTwins.Core;
using Azure.DigitalTwins.Core.Models;
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
    private readonly double _tempMinC;
    private readonly double _tempMaxC;

    public ProcessAlertRules(ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<ProcessAlertRules>();

        var adtUrl = GetRequired("ADT_SERVICE_URL");
        _dt = new DigitalTwinsClient(new Uri(adtUrl), new DefaultAzureCredential());

        var sbConn = GetRequired("ALERTS_SERVICEBUS_CONNECTION");
        var topicName = GetRequired("ALERTS_TOPIC_NAME");
        var sbClient = new ServiceBusClient(sbConn);
        _sender = sbClient.CreateSender(topicName);

        _tempMinC = GetDouble("RULE_TEMP_MIN_C", 18);
        _tempMaxC = GetDouble("RULE_TEMP_MAX_C", 22);
    }

    [Function("ProcessAlertRules")]
    public async Task Run([TimerTrigger("%RULE_CRON%")] TimerInfo timer, FunctionContext context)
    {
        var ct = context.CancellationToken;
        var query =
            $"SELECT * FROM digitaltwins WHERE IS_OF_MODEL('{RoomModelId}') AND (tempC < {_tempMinC} OR tempC > {_tempMaxC})";

        var count = 0;
        var twins = _dt.QueryAsync<BasicDigitalTwin>(query, cancellationToken: ct);
        
        await foreach (var twin in twins)
        {
            var gontsa = twin.Contents;
            var (tempC, hasTemp) = TryGetDouble(, "tempC");
            if (!hasTemp)
            {
                continue;
            }

            var roomId = GetString(twin.Contents, "roomId") ?? twin.Id;
            var breach = tempC < _tempMinC ? "Low" : "High";

            var alert = new AlertMessage(
                AlertTypeTemp,
                AlertSeverityTemp,
                twin.Id,
                roomId,
                DateTimeOffset.UtcNow,
                tempC,
                _tempMinC,
                _tempMaxC,
                breach
            );

            var message = new ServiceBusMessage(JsonSerializer.Serialize(alert));
            message.ApplicationProperties["AlertType"] = alert.AlertType;
            message.ApplicationProperties["Severity"] = alert.Severity;
            message.ApplicationProperties["RoomId"] = alert.RoomId;
            message.ApplicationProperties["TwinId"] = alert.TwinId;

            await _sender.SendMessageAsync(message, ct);
            count++;
        }

        _log.LogInformation(
            "ProcessAlertRules evaluated temp range {MinTempC}-{MaxTempC}C and emitted {Count} alerts",
            _tempMinC,
            _tempMaxC,
            count);
    }

    private static string GetRequired(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing {name}");

    private static double GetDouble(string name, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, out var value) ? value : fallback;
    }

    private static string? GetString(IReadOnlyDictionary<string, object> contents, string key)
    {
        if (!contents.TryGetValue(key, out var raw))
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };
    }

    private static (double value, bool ok) TryGetDouble(IReadOnlyDictionary<string, object> contents, string key)
    {
        if (!contents.TryGetValue(key, out var raw))
            return (0, false);

        return raw switch
        {
            double d => (d, true),
            float f => (f, true),
            int i => (i, true),
            long l => (l, true),
            decimal m => ((double)m, true),
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out var d) => (d, true),
            _ => (0, false)
        };
    }

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
}
