using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml;

namespace IpMonitor;

internal class Program
{
    private const string xmlFilePath = "IpsAvailabilityResults.xml";

    private static readonly ConcurrentQueue<(string ip, string time, bool success)> pingResultsQueue = new();
    private static bool isPingMonitoringComplete;

    public static async Task Main()
    {
        Console.WriteLine("Enter your input in format: testTimeInSeconds IP1 IP2 ...");
        var pars = Console.ReadLine();
        var splitPars = pars.Split(" ");
        int.TryParse(splitPars[0], out var testDuration);
        var ipAddresses = splitPars.Skip(1).ToList();

        File.Delete(xmlFilePath);
        Console.WriteLine($"Starting test for {testDuration} seconds on IPs: {string.Join(", ", ipAddresses)}");

        var writerTask = Task.Run(WriteResultsAsync);

        var tasks = new List<Task>();
        foreach (var ip in ipAddresses)
            tasks.Add(PingMonitorAsync(ip, testDuration));
        await Task.WhenAll(tasks);

        isPingMonitoringComplete = true;

        await writerTask;

        await ReadResultsAsync();
    }

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
            await Task.Delay(100);
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