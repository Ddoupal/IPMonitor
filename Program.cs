using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IpMonitor;

internal class Program
{
    private const string xmlFilePath = "IpsAvailabilityResults.xml";

    private static readonly ConcurrentQueue<(string ip, string time, bool success)> pingResultsQueue = new();
    private static bool isPingMonitoringComplete;

    public static async Task Main()
    {
        var (testDuration, ipAddresses) = GetValidatedInput();

        File.Delete(xmlFilePath);
        Console.WriteLine($"Starting test for {testDuration} seconds on IPs: {string.Join(", ", ipAddresses)}");

        var writerTask = Task.Run(WriteResultsAsync);

        var tasks = ipAddresses
            .Select(ip => PingMonitorAsync(ip, testDuration))
            .ToList();
        await Task.WhenAll(tasks);

        isPingMonitoringComplete = true;

        await writerTask;

        Console.WriteLine("Starting to process data from XML");

        await ReadResultsAsync();
    }

    private static (int, List<string>) GetValidatedInput()
    {
        Console.WriteLine("Enter your input in format: testTimeInSeconds IP1 IP2 ...");
        const string ipPattern = @"\b((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(\.|$)){4}\b";
        int testDuration;
        List<string> ipAddresses;

        while (true)
        {
            var pars = Console.ReadLine();
            var splitPars = pars?.Split(" ") ?? Array.Empty<string>();

            if (splitPars.Length < 2 ||
                !int.TryParse(splitPars[0], out testDuration) ||
                splitPars.Skip(1).Any(ip => !Regex.IsMatch(ip, ipPattern)))
            {
                Console.WriteLine("Wrong input format, Please enter your input in format: testTimeInSeconds IP1 IP2 ...");
                continue;
            }

            ipAddresses = splitPars.Skip(1).ToList();
            break;
        }
        return (testDuration, ipAddresses);
    }

    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    //The smallest possible timeout is currently around 500ms even if you set less, possible fix will be in future https://github.com/dotnet/runtime/issues/102445
    //------------------------------------------------------------------------------------------------------------------------------------------------------------
    private static async Task PingMonitorAsync(string ip, int duration)
    {
        using Ping ping = new();
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed.TotalSeconds < duration)
        {
            var startTime = Stopwatch.GetTimestamp();
            var response = await ping.SendPingAsync(ip, 300);
            var stopTime = Stopwatch.GetTimestamp();
            var elapsedTime = (stopTime - startTime) * 1000.0 / Stopwatch.Frequency;

            pingResultsQueue.Enqueue((ip, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"), response.Status == IPStatus.Success));

            var remainingTime = Math.Max(0, 100 - elapsedTime);
            await Task.Delay((int)remainingTime);
        }
    }

    private static async Task WriteResultsAsync()
    {
        await using var writer = XmlWriter.Create(xmlFilePath, new XmlWriterSettings { Indent = true, Async = true, Encoding = Encoding.UTF8 });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "PingResults", null);

        while (!isPingMonitoringComplete || !pingResultsQueue.IsEmpty)
        {
            while (pingResultsQueue.TryDequeue(out var result))
            {
                await writer.WriteStartElementAsync(null, "PingResult", null);
                await writer.WriteAttributeStringAsync(null, "IP", null, result.ip);
                await writer.WriteAttributeStringAsync(null, "Time", null, result.time);
                await writer.WriteAttributeStringAsync(null, "Success", null, result.success.ToString());
                await writer.WriteEndElementAsync();
            }
            await Task.Delay(100);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }

    private static async Task ReadResultsAsync()
    {
        var results = new Dictionary<string, (int total, int success)>();

        using var reader = XmlReader.Create(xmlFilePath, new XmlReaderSettings { Async = true });
        while (await reader.ReadAsync())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "PingResult")
            {
                var ip = reader.GetAttribute("IP");
                var success = bool.Parse(reader.GetAttribute("Success") ?? "false");

                if (string.IsNullOrEmpty(ip))
                    continue;

                if (!results.ContainsKey(ip))
                    results[ip] = (0, 0);
                results[ip] = (results[ip].total + 1, results[ip].success + (success ? 1 : 0));
            }
        }

        Console.WriteLine("------------RESULTS-------------");

        foreach (var (ip, (total, success)) in results)
        {
            var availability = (success / (double)total) * 100;
            Console.WriteLine($"{ip} - {availability:F2}% availability");
        }
    }
}