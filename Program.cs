using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace IpMonitor;

internal class Program
{
    private const string xmlFilePath = "IpsAvailabilityResults.xml";
    private static readonly ILogger<Program> logger = LoggerFactory
        .Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider($"log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"));
        })
        .CreateLogger<Program>();

    private static readonly ConcurrentQueue<(string ip, string time, bool success)> pingResultsQueue = new();
    private static bool isPingMonitoringComplete;
    private static DateTimeOffset dateStartTest;
    private static DateTimeOffset dateEndTest;

    public static async Task Main()
    {
        var (testDuration, ipAddresses) = GetValidatedInput();

        File.Delete(xmlFilePath);
        logger.LogInformation("Starting test for {testDuration} seconds on IPs: {ips)}", testDuration, string.Join(", ", ipAddresses));

        var writerTask = Task.Run(WriteResultsAsync);

        dateStartTest = DateTimeOffset.UtcNow;
        var tasks = ipAddresses
            .Select(ip => PingMonitorAsync(ip, testDuration))
            .ToList();
        await Task.WhenAll(tasks);
        dateEndTest = DateTimeOffset.UtcNow;
        isPingMonitoringComplete = true;

        await writerTask;

        logger.LogInformation("Starting to process data from XML");

        await ReadResultsAsync();

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
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
            try
            {
                var startTime = Stopwatch.GetTimestamp();
                var response = await ping.SendPingAsync(ip, 300);
                var stopTime = Stopwatch.GetTimestamp();
                var elapsedTime = (stopTime - startTime) * 1000.0 / Stopwatch.Frequency;

                pingResultsQueue.Enqueue((ip, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"), response.Status == IPStatus.Success));

                var remainingTime = Math.Max(0, 100 - elapsedTime);
                await Task.Delay((int)remainingTime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in ping monitoring for {ip}", ip);
            }
        }
    }

    private static async Task WriteResultsAsync()
    {
        try
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while writing results to xml");
        }
    }

    private static async Task ReadResultsAsync()
    {
        var results = new Dictionary<string, (int total, int success)>();
        try
        {
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
        }
        catch (Exception ex) 
        {
            logger.LogError(ex, "Error while reading results from xml");
        }

        Console.WriteLine("------------RESULTS-------------");

        logger.LogInformation("Test run from {dateStartTest} to {dateEndTest}", dateStartTest.ToLocalTime(), dateEndTest.ToLocalTime());

        foreach (var (ip, (total, success)) in results)
        {
            var availability = success / (double)total * 100;
            var resultText = $"{ip} - {availability:F2}% availability - total ping tries: {total} ({success} successfull)";
            logger.LogInformation(resultText);
        }
    }
}