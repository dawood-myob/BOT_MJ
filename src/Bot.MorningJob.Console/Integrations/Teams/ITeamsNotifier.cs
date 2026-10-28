using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bot.MorningJob.Console.Integrations.Teams
{
    /// <summary>
    /// Posts the daily morning report to a Microsoft Teams channel through a Power Automate
    /// "When a Teams webhook request is received" workflow. The workflow only accepts Adaptive
    /// Card payloads, so the report text is converted into a styled card first.
    /// </summary>
    public interface ITeamsNotifier
    {
        /// <summary>
        /// Converts the report text into an Adaptive Card and posts it to the configured webhook.
        /// Never throws; returns false (after logging) when the post was skipped or failed.
        /// </summary>
        Task<bool> PostReportAsync(string reportText);
    }

    public class TeamsNotifier : ITeamsNotifier
    {
        public const string WebhookEnvironmentVariable = "TEAMS_FLOW_WEBHOOK_URL";

        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly Regex SectionHeaderRegex = new(@"^\*(?<title>.+?)\*(?<extra>.*)$", RegexOptions.Compiled);
        private static readonly Regex ProblemLineRegex = new(@"(?<!\d)[1-9]\d* FAILED|\bFailed\b|[1-9]\d* Data Errors?\(s\)|COMPLETED WITH ERROR", RegexOptions.Compiled);
        private static readonly Regex OkLineRegex = new(@"COMPLETED\s+OK", RegexOptions.Compiled);

        private readonly ILogger<TeamsNotifier> _logger;
        private readonly TeamsSettings _settings;

        public TeamsNotifier(ILogger<TeamsNotifier> logger, IOptions<AppSettings> appSettings)
        {
            _logger = logger;
            _settings = appSettings.Value.Teams ?? new TeamsSettings();
        }

        public async Task<bool> PostReportAsync(string reportText)
        {
            var webhookUrl = string.IsNullOrWhiteSpace(_settings.WebhookUrl)
                ? Environment.GetEnvironmentVariable(WebhookEnvironmentVariable)
                : _settings.WebhookUrl;

            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                _logger.LogInformation(
                    "Teams webhook is not configured (AppSettings:Teams:WebhookUrl or {EnvironmentVariable}). Skipping Teams post.",
                    WebhookEnvironmentVariable);
                return false;
            }

            if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) || webhookUri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogWarning("Teams webhook URL is not a valid HTTPS URL. Skipping Teams post.");
                return false;
            }

            try
            {
                var payload = BuildCardPayload(reportText);
                using var response = await HttpClient.PostAsJsonAsync(webhookUri, payload);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Teams workflow request failed with HTTP {StatusCode}. Response: {Body}", (int)response.StatusCode, body);
                    return false;
                }

                _logger.LogInformation("Teams workflow accepted the report card (HTTP {StatusCode}). Check the workflow run history to confirm delivery.", (int)response.StatusCode);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Deliberately not logging the exception object: its text can include the webhook URL.
                _logger.LogWarning("Could not post the report to Teams ({ExceptionType}).", ex.GetType().Name);
                return false;
            }
        }

        private sealed record Section(string Title, string Extra, List<string> Lines);

        private static Dictionary<string, object> BuildCardPayload(string reportText)
        {
            var lines = reportText.Replace("\r\n", "\n").Split('\n');

            var headingLines = new List<string>();
            var sections = new List<Section>();
            Section? current = null;
            var seenDivider = !lines.Any(l => l.TrimStart().StartsWith('\u2501'));

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (!seenDivider)
                {
                    if (line.StartsWith('\u2501'))
                    {
                        seenDivider = true;
                    }
                    else if (line.Length > 0)
                    {
                        headingLines.Add(line);
                    }

                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                var header = SectionHeaderRegex.Match(line);
                if (header.Success)
                {
                    var extra = header.Groups["extra"].Value.Trim().TrimStart('-').Trim();
                    current = new Section(header.Groups["title"].Value.Trim(), extra, new List<string>());
                    sections.Add(current);
                }
                else
                {
                    current?.Lines.Add(line);
                }
            }

            var body = new List<object>();

            if (headingLines.Count > 0)
            {
                var headerItems = new List<object>
                {
                    TextBlock(headingLines[0], size: "Large", weight: "Bolder")
                };

                foreach (var subtitle in headingLines.Skip(1))
                {
                    headerItems.Add(TextBlock(subtitle, size: "Medium", spacing: "Small"));
                }

                body.Add(new Dictionary<string, object>
                {
                    ["type"] = "Container",
                    ["style"] = "accent",
                    ["items"] = headerItems
                });
            }

            foreach (var section in sections)
            {
                var title = $"{IconFor(section.Title)} {section.Title}";

                // A header with no content of its own (e.g. "Pay Global Issues") groups the sections below it.
                if (section.Lines.Count == 0 && section.Extra.Length == 0)
                {
                    body.Add(TextBlock(title, size: "Large", weight: "Bolder", color: "Accent", spacing: "Large", separator: true));
                    continue;
                }

                var items = new List<object>
                {
                    TextBlock(title, size: "Medium", weight: "Bolder", color: "Accent")
                };

                if (section.Extra.Length > 0)
                {
                    items.Add(TextBlock(section.Extra, spacing: "Small", isSubtle: true));
                }

                foreach (var line in section.Lines)
                {
                    items.Add(BuildLineBlock(line));
                }

                body.Add(new Dictionary<string, object>
                {
                    ["type"] = "Container",
                    ["style"] = "emphasis",
                    ["spacing"] = "Medium",
                    ["items"] = items
                });
            }

            var card = new Dictionary<string, object>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.2",
                ["msteams"] = new Dictionary<string, object> { ["width"] = "Full" },
                ["body"] = body
            };

            return new Dictionary<string, object>
            {
                ["type"] = "message",
                ["attachments"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["contentType"] = "application/vnd.microsoft.card.adaptive",
                        ["content"] = card
                    }
                }
            };
        }

        private static Dictionary<string, object> BuildLineBlock(string line)
        {
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return TextBlock($"[{line}]({line})", spacing: "Small");
            }

            var text = line;
            if (text.Length > 1 && !char.IsLetterOrDigit(text[0]))
            {
                // Bullet lines: guarantee exactly one space between the bullet symbol and the text.
                text = $"{text[0]} {text[1..].TrimStart()}";
            }

            string? color = null;
            if (ProblemLineRegex.IsMatch(text))
            {
                color = "Attention";
            }
            else if (OkLineRegex.IsMatch(text))
            {
                color = "Good";
            }

            return TextBlock(text, spacing: "Small", color: color);
        }

        private static string IconFor(string title)
        {
            if (title.Contains("Pay Global", StringComparison.OrdinalIgnoreCase)) return "\U0001F465";
            if (title.Contains("INT002", StringComparison.OrdinalIgnoreCase)) return "\U0001F4E5";
            if (title.Contains("INT003", StringComparison.OrdinalIgnoreCase)) return "\U0001F4CA";
            if (title.Contains("Banking", StringComparison.OrdinalIgnoreCase)) return "\U0001F3E6";
            if (title.Contains("Acumatica", StringComparison.OrdinalIgnoreCase)) return "\U0001F9FE";
            if (title.Contains("Stripe", StringComparison.OrdinalIgnoreCase)) return "\U0001F4B3";
            if (title.Contains("Sumo", StringComparison.OrdinalIgnoreCase)) return "\U0001F6A8";
            if (title.Equals("DB", StringComparison.OrdinalIgnoreCase)) return "\U0001F5C4";
            if (title.Contains("Miscellaneous", StringComparison.OrdinalIgnoreCase)) return "\U0001F9E9";
            if (title.Equals("XE", StringComparison.OrdinalIgnoreCase)) return "\U0001F4B1";
            if (title.Contains("Community", StringComparison.OrdinalIgnoreCase)) return "\U0001F310";
            if (title.Contains("Follow Up", StringComparison.OrdinalIgnoreCase)) return "\U0001F4DD";
            return "\U0001F4CC";
        }

        private static Dictionary<string, object> TextBlock(
            string text,
            string? size = null,
            string? weight = null,
            string? color = null,
            string? spacing = null,
            bool separator = false,
            bool isSubtle = false)
        {
            var block = new Dictionary<string, object>
            {
                ["type"] = "TextBlock",
                ["text"] = text,
                ["wrap"] = true
            };

            if (size != null) block["size"] = size;
            if (weight != null) block["weight"] = weight;
            if (color != null) block["color"] = color;
            if (spacing != null) block["spacing"] = spacing;
            if (separator) block["separator"] = true;
            if (isSubtle) block["isSubtle"] = true;

            return block;
        }
    }
}
