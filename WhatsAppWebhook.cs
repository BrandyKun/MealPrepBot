using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;

namespace MealReminderFunction;

public class WhatsAppWebhook
{
    private readonly ILogger<WhatsAppWebhook> _logger;
    private readonly HttpClient _http = new();

    public WhatsAppWebhook(ILogger<WhatsAppWebhook> logger)
    {
        _logger = logger;
    }

    [Function("WhatsAppWebhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
    {
        // Meta verification handshake - GET request
        if (req.Method == "GET")
        {
            var mode = req.Query["hub.mode"].ToString();
            var token = req.Query["hub.verify_token"].ToString();
            var challenge = req.Query["hub.challenge"].ToString();

            var verifyToken = Environment.GetEnvironmentVariable("WhatsAppVerifyToken");

            if (mode == "subscribe" && token == verifyToken)
            {
                _logger.LogInformation("Webhook verified");
                return new OkObjectResult(int.Parse(challenge));
            }

            return new UnauthorizedResult();
        }

        // Incoming message - POST request
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        _logger.LogInformation("Incoming webhook: {body}", body);

        try
        {
            var payload = JObject.Parse(body);

            // Extract the message text and sender
            var entry = payload["entry"]?[0];
            var changes = entry?["changes"]?[0];
            var value = changes?["value"];
            var messages = value?["messages"];

            if (messages == null) return new OkResult();

            var message = messages[0];
            var from = message?["from"]?.ToString();
            var text = message?["text"]?["body"]?.ToString();

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from))
                return new OkResult();

            _logger.LogInformation("Message from {from}: {text}", from, text);

            // Call Claude API
            var claudeReply = await CallClaude(text);

            // Send reply back via WhatsApp
            await SendWhatsApp(from, claudeReply);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error processing webhook: {error}", ex.Message);
        }

        return new OkResult();
    }

    private async Task<string> CallClaude(string userMessage)
    {
        var anthropicKey = Environment.GetEnvironmentVariable("AnthropicApiKey");

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("x-api-key", anthropicKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var payload = new
        {
            model = "claude-sonnet-4-20250514",
            max_tokens = 300,
            system = """
                You are a helpful meal and fitness assistant for Brandy.
                Brandy is tracking calories targeting 2,098 kcal per day.
                Brandy batch cooks on Sundays - meals include sweet chilli chicken rice bowls and chilli con carne.
                Keep replies short and friendly - this is a WhatsApp chat.
                If asked about calories or meals, be specific and helpful.
                """,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        var response = await _http.PostAsJsonAsync(
            "https://api.anthropic.com/v1/messages", payload);

        var responseBody = await response.Content.ReadAsStringAsync();
        var json = JObject.Parse(responseBody);
        return json["content"]?[0]?["text"]?.ToString() ?? "Sorry, I couldn't process that.";
    }

    private async Task SendWhatsApp(string to, string message)
    {
        var token = Environment.GetEnvironmentVariable("WhatsAppToken");
        var phoneId = Environment.GetEnvironmentVariable("PhoneNumberId");

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            messaging_product = "whatsapp",
            to = to,
            type = "text",
            text = new { body = message }
        };

        var response = await _http.PostAsJsonAsync(
            $"https://graph.facebook.com/v20.0/{phoneId}/messages", payload);

        _logger.LogInformation("WhatsApp reply status: {status}", response.StatusCode);
    }
}