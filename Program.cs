using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

var foundryEndpoint = (config["AZURE_AI_ENDPOINT"]
    ?? throw new InvalidOperationException("Set AZURE_AI_ENDPOINT.")).TrimEnd('/');
var geminiKey = config["GEMINI_API_KEY"]
    ?? throw new InvalidOperationException("Set GEMINI_API_KEY.");
var runs = int.Parse(config["RUNS"] ?? "5");
// Only needed behind a proxy or in tests. The default is Google's API.
var geminiEndpoint = (config["GEMINI_ENDPOINT"] ?? "https://generativelanguage.googleapis.com").TrimEnd('/');

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

IDecisionClient[] clients =
[
    new MicrosoftClient(http, foundryEndpoint,
        deployment: config["AZURE_AI_DEPLOYMENT"] ?? "ms-decision-1.1",
        apiKey: config["AZURE_AI_API_KEY"],        // optional: without it, az login (Entra ID) is used
        credential: new DefaultAzureCredential()),
    new GeminiClient(http, geminiKey, geminiEndpoint)
];

// The same questions go to both services. Only the request JSON differs.
var cases = new Case[]
{
    new("ticket-text",
        "Checkout is down again for all EU customers. This is my third request since 09:00.",
        FilePath: null,
        [
            new("team", QType.Choice, "Which team should handle this request? Pick the primary problem.",
                Options: new()
                {
                    ["billing"] = "Charges, invoices, refunds, or subscription payments",
                    ["engineering"] = "Bugs, errors, outages, or integration failures",
                    ["support"] = "How-to questions, passwords, and account access"
                }),
            new("severity", QType.Score, "How severe is this incident?",
                Levels: ["Cosmetic", "Minor", "Major", "Critical"]),
            new("repeat_contact", QType.Noul, "Has the customer contacted support about this before?",
                TrueText: "The text says this is a repeat or follow-up",
                FalseText: "Nothing in the text indicates an earlier contact")
        ]),

    new("screenshot",
        "Customer says checkout failed and attached a screenshot.",
        "checkout-error.png",
        [
            new("error_visible", QType.Noul, "Does the screenshot show an error message?"),
            new("problem", QType.Choice, "What problem does the screenshot show?",
                Options: new()
                {
                    ["payment_declined"] = "A card payment was declined or failed",
                    ["page_error"] = "A site or server error page",
                    ["other"] = "Anything else, or no problem is visible"
                })
        ]),

    new("invoice-pdf",
        "A supplier invoice for accounts payable.",
        "invoice.pdf",
        [
            new("has_po", QType.Noul, "Does the invoice show a purchase order number?"),
            new("over_5000", QType.Noul, "Is the total due more than 5,000?")
        ])
};

var summary = new List<(string Case, string Service, string Latency, string Tokens, string Cost)>();

foreach (var c in cases)
{
    Console.WriteLine($"###################### {c.Name} ######################");
    var results = clients.ToDictionary(x => x.Name, _ => new List<Run>());
    var unsupported = new HashSet<string>();

    // Alternate the services on every round, so both see the same network conditions.
    for (var i = 0; i < runs; i++)
    {
        foreach (var client in clients)
        {
            if (unsupported.Contains(client.Name)) continue;
            var run = await client.RunAsync(c);
            if (run is null) { unsupported.Add(client.Name); continue; }
            results[client.Name].Add(run);
        }
    }

    foreach (var client in clients)
    {
        Console.WriteLine($"-- {client.Name}");
        if (unsupported.Contains(client.Name))
        {
            Console.WriteLine("   not supported for this input");
            summary.Add((c.Name, client.Name, "n/a", "n/a", "n/a"));
            continue;
        }

        var list = results[client.Name];
        foreach (var answer in list[0].Answers) Console.WriteLine($"   {answer}");

        var median = list.Select(r => r.Milliseconds).Order().ElementAt(list.Count / 2);
        var tokens = list[0].InputTokens;
        var cost = tokens is null ? "n/a" : $"${tokens.Value * client.PricePerMillionInput / 1_000_000m:0.000000}";
        Console.WriteLine($"   median {median:0} ms over {list.Count} runs, input tokens {tokens?.ToString() ?? "n/a"}, est. cost {cost}");
        summary.Add((c.Name, client.Name, $"{median:0} ms", tokens?.ToString() ?? "n/a", cost));
    }
    Console.WriteLine();
}

Console.WriteLine($"{"case",-14} {"service",-24} {"latency",10} {"in tokens",10} {"est. cost",12}");
foreach (var s in summary)
{
    Console.WriteLine($"{s.Case,-14} {s.Service,-24} {s.Latency,10} {s.Tokens,10} {s.Cost,12}");
}

// ---------------------------------------------------------------------------------------------

enum QType { Noul, Choice, Score }

record Question(string Name, QType Type, string Instructions,
    Dictionary<string, string>? Options = null, string[]? Levels = null,
    string? TrueText = null, string? FalseText = null);

record Case(string Name, string Text, string? FilePath, Question[] Questions);

record Run(double Milliseconds, long? InputTokens, List<string> Answers);

interface IDecisionClient
{
    string Name { get; }
    decimal PricePerMillionInput { get; }   // USD per 1M input tokens. Output tokens are not billed by either service.
    Task<Run?> RunAsync(Case c);            // null = this service doesn't take that input
}

/// <summary>Microsoft-Decision-1.1 on a Microsoft Foundry resource: POST {endpoint}/providers/microsoft/v1/systemone.</summary>
sealed class MicrosoftClient(HttpClient http, string endpoint, string deployment, string? apiKey, TokenCredential credential)
    : IDecisionClient
{
    public string Name => "Microsoft-Decision-1.1";
    public decimal PricePerMillionInput => 0.042m;   // Global Standard, per Microsoft's launch pricing: check the pricing page

    public async Task<Run?> RunAsync(Case c)
    {
        var body = new Dictionary<string, object> { ["model"] = deployment };   // the deployment name, not the model name

        if (c.FilePath is null)
        {
            body["state"] = c.Text;
        }
        else if (Path.GetExtension(c.FilePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            // Images go in "assets", keyed by name, and the text refers to them as image:<name>.
            var name = Path.GetFileName(c.FilePath);
            body["state"] = $"{c.Text} Screenshot: image:{name}";
            body["assets"] = new Dictionary<string, object>
            {
                [name] = new { type = "image", format = "png", data_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(c.FilePath)) }
            };
        }
        else
        {
            return null;   // the Learn page documents images only
        }

        body["questions"] = c.Questions.ToDictionary(q => q.Name, q => (object)ToJson(q));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/providers/microsoft/v1/systemone");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), CancellationToken.None);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else
        {
            request.Headers.Add("api-key", apiKey);
        }

        return await Shared.SendAsync(http, request, body, c.Questions);
    }

    static object ToJson(Question q) => q.Type switch
    {
        QType.Choice => new { type = "choice", instructions = q.Instructions, criteria = q.Options },
        QType.Score => new { type = "score", instructions = q.Instructions, criteria = q.Levels },
        _ when q.TrueText is not null => new
        {
            type = "noul", instructions = q.Instructions,
            criteria = new Dictionary<string, string> { ["true"] = q.TrueText, ["false"] = q.FalseText! }
        },
        _ => new { type = "noul", instructions = q.Instructions }
    };
}

/// <summary>Gemini Decision 1 on the Gemini Developer API (v1beta): POST /v1beta/decisions.</summary>
sealed class GeminiClient(HttpClient http, string apiKey, string endpoint) : IDecisionClient
{
    public string Name => "Gemini Decision 1";
    public decimal PricePerMillionInput => 0.10m;    // per Google's announcement: check the pricing page

    public async Task<Run?> RunAsync(Case c)
    {
        // "input" is a list of blocks: text, image, audio, video or document (PDF).
        var input = new List<object> { new { type = "text", text = c.Text } };
        if (c.FilePath is not null)
        {
            var (blockType, mime) = Path.GetExtension(c.FilePath).ToLowerInvariant() switch
            {
                ".png" => ("image", "image/png"),
                ".pdf" => ("document", "application/pdf"),
                _ => throw new InvalidOperationException($"No block type for {c.FilePath}")
            };
            input.Add(new { type = blockType, data = Convert.ToBase64String(await File.ReadAllBytesAsync(c.FilePath)), mime_type = mime });
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = "gemini-decision-1",
            ["input"] = input,
            ["questions"] = c.Questions.ToDictionary(q => q.Name, q => (object)ToJson(q))
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1beta/decisions");
        request.Headers.Add("x-goog-api-key", apiKey);

        return await Shared.SendAsync(http, request, body, c.Questions);
    }

    static object ToJson(Question q) => q.Type switch
    {
        QType.Choice => new { type = "choice", instructions = q.Instructions, criteria = q.Options },
        QType.Score => new { type = "score", instructions = q.Instructions, criteria = q.Levels },
        _ when q.TrueText is not null => new
        {
            type = "noul", instructions = q.Instructions,
            criteria = new { true_description = q.TrueText, false_description = q.FalseText }
        },
        _ => new { type = "noul", instructions = q.Instructions }
    };
}

static class Shared
{
    // Both services answer with the same shape for the three question types.
    public static async Task<Run> SendAsync(HttpClient http, HttpRequestMessage request, object body, Question[] questions)
    {
        // StringContent sets a fixed Content-Length; JsonContent uses chunked encoding.
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var timer = Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        timer.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} from {request.RequestUri?.Host}: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var answers = root.TryGetProperty("answers", out var a) ? a : root;

        var lines = new List<string>();
        foreach (var q in questions)
        {
            lines.Add($"{q.Name}: {Describe(q, answers.GetProperty(q.Name))}");
        }

        return new Run(timer.Elapsed.TotalMilliseconds, ReadInputTokens(root), lines);
    }

    static string Describe(Question q, JsonElement answer)
    {
        // "confidence" is optional: Gemini returns it on every answer, Microsoft on choice and score answers.
        var confidence = answer.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
            ? $", confidence {c.GetDouble():0.00}" : "";

        switch (q.Type)
        {
            case QType.Choice:
                var label = answer.GetProperty("choice").GetString()!;
                var p = answer.GetProperty("probabilities").GetProperty(label).GetDouble();
                return $"{label} (p={p:0.00}){confidence}";
            case QType.Score:
                return $"{answer.GetProperty("score").GetDouble():0.00}{confidence}";
            default:
                return $"p(true)={answer.GetProperty("noul").GetDouble():0.00}{confidence}";
        }
    }

    // Gemini reports total_input_tokens. For Microsoft the Learn page only says the response includes token usage,
    // so look for the first numeric field with "input" or "prompt" in its name.
    static long? ReadInputTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in usage.EnumerateObject())
        {
            var n = property.Name.ToLowerInvariant();
            if (property.Value.ValueKind == JsonValueKind.Number && (n.Contains("input") || n.Contains("prompt")))
            {
                return property.Value.GetInt64();
            }
        }
        return null;
    }
}
