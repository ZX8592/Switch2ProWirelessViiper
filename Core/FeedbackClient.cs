using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Switch2ProWirelessViiper.Core;

public sealed record FeedbackSubmissionResult(string RequestId, int Attempts);

public sealed class FeedbackSubmissionException(
    HttpStatusCode statusCode,
    string responseBody) : Exception($"Feedback service returned HTTP {(int)statusCode} ({statusCode}): {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public static class FeedbackClient
{
    private const string FeedbackEndpoint = "https://feedback.zx8592.top/v1/feedback";
    private const int MaximumFeedbackBytes = 32 * 1024;
    private const int MaximumZipBytes = 15 * 1024 * 1024;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
    ];

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(180),
    };

    public static async Task<FeedbackSubmissionResult> SubmitAsync(
        string feedback,
        bool includeDiagnosticLogs,
        string language,
        Action<string>? trace,
        CancellationToken cancellationToken)
    {
        feedback = feedback.Trim();
        var feedbackBytes = Encoding.UTF8.GetByteCount(feedback);
        if (feedbackBytes == 0)
        {
            throw new ArgumentException("Feedback text is empty.", nameof(feedback));
        }

        if (feedbackBytes > MaximumFeedbackBytes)
        {
            throw new ArgumentException(
                $"Feedback text is {feedbackBytes} UTF-8 bytes; the limit is {MaximumFeedbackBytes} bytes.",
                nameof(feedback));
        }

        var zip = BuildDiagnosticArchive(includeDiagnosticLogs, language);
        if (zip.Length > MaximumZipBytes)
        {
            throw new InvalidOperationException(
                $"Diagnostic archive is {zip.Length} bytes; the service limit is {MaximumZipBytes} bytes.");
        }

        for (var attempt = 1; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            using var feedbackContent = new StringContent(feedback, Encoding.UTF8, "text/plain");
            using var fileContent = new ByteArrayContent(zip);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            // The relay requires exactly these two fields in this order.
            form.Add(feedbackContent, "feedback");
            form.Add(fileContent, "file", "Switch2ProWirelessViiper-feedback.zip");

            trace?.Invoke(
                $"Submitting feedback: attempt={attempt}, textBytes={feedbackBytes}, " +
                $"zipBytes={zip.Length}, diagnostics={includeDiagnosticLogs}.");
            using var response = await Client
                .PostAsync(FeedbackEndpoint, form, cancellationToken)
                .ConfigureAwait(false);
            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var requestId = ReadRequestId(responseBody);
                trace?.Invoke($"Feedback accepted: requestId={requestId}, attempts={attempt}.");
                return new FeedbackSubmissionResult(requestId, attempt);
            }

            if (IsTransient(response.StatusCode) && attempt <= RetryDelays.Length)
            {
                var delay = RetryDelays[attempt - 1];
                trace?.Invoke(
                    $"Feedback service returned HTTP {(int)response.StatusCode}; " +
                    $"retrying in {delay.TotalSeconds:F0}s.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new FeedbackSubmissionException(response.StatusCode, Limit(responseBody, 512));
        }
    }

    private static byte[] BuildDiagnosticArchive(bool includeDiagnosticLogs, string language)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metadata = archive.CreateEntry("system-info.txt", CompressionLevel.SmallestSize);
            using (var writer = new StreamWriter(metadata.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                var version = typeof(FeedbackClient).Assembly.GetName().Version?.ToString() ?? "unknown";
                writer.WriteLine($"Captured: {DateTimeOffset.Now:O}");
                writer.WriteLine($"App version: {version}");
                writer.WriteLine($"OS: {RuntimeInformation.OSDescription}");
                writer.WriteLine($"Framework: {RuntimeInformation.FrameworkDescription}");
                writer.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
                writer.WriteLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
                writer.WriteLine($"UI language: {language}");
                writer.WriteLine($"Diagnostic logs included: {includeDiagnosticLogs}");
                writer.WriteLine("settings.json included: false");
            }

            if (includeDiagnosticLogs)
            {
                var directory = Path.GetDirectoryName(AppSettings.SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    AddFileIfPresent(archive, Path.Combine(directory, "diagnostics.log"), "diagnostics.log");
                    AddFileIfPresent(archive, Path.Combine(directory, "diagnostics.previous.log"), "diagnostics.previous.log");
                    AddFileIfPresent(archive, Path.Combine(directory, "crash.log"), "crash.log");
                }
            }
        }

        return output.ToArray();
    }

    private static void AddFileIfPresent(ZipArchive archive, string path, string entryName)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
        catch
        {
            // A locked or disappearing log should not prevent the user from sending feedback.
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static string ReadRequestId(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (json.RootElement.TryGetProperty("request_id", out var requestId) &&
                requestId.ValueKind == JsonValueKind.String)
            {
                return requestId.GetString() ?? "unknown";
            }
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "...";
}
