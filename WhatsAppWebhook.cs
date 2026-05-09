using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

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
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req
    )
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
        // Incoming message - POST request
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        _logger.LogInformation("Raw body: {body}", body);
        _logger.LogInformation("Content type: {ct}", req.ContentType);

        try
        {
            // Twilio sends form data, not JSON
            string from = "";
            string text = "";

            if (req.ContentType != null && req.ContentType.Contains("application/x-www-form-urlencoded"))
            {
                // Parse form data
                var formData = System.Web.HttpUtility.ParseQueryString(body);
                from = formData["From"]?.Replace("whatsapp:+", "") ?? "";
                text = formData["Body"] ?? "";
            }
            else
            {
                // Try Meta JSON format as fallback
                var payload = JObject.Parse(body);
                var messages = payload["entry"]?[0]?["changes"]?[0]?["value"]?["messages"];
                if (messages == null) return new OkResult();
                from = messages[0]?["from"]?.ToString() ?? "";
                text = messages[0]?["text"]?["body"]?.ToString() ?? "";
            }

            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from))
                return new OkResult();

            _logger.LogInformation("Message from {from}: {text}", from, text);

            var claudeReply = await CallClaude(text);
            SendWhatsApp(from, claudeReply);
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
            model = "claude-sonnet-4-5",
            max_tokens = 300,
            system = """
                You are a helpful meal and fitness assistant for Brandy.
                Brandy is tracking calories targeting 2,098 kcal per day.
                Brandy batch cooks on Sundays - meals include sweet chilli chicken rice bowls and chilli con carne.
                Keep replies short and friendly - this is a WhatsApp chat.
                If asked about calories or meals, be specific and helpful.
                """,
            messages = new[] { new { role = "user", content = userMessage } },
        };

        var response = await _http.PostAsJsonAsync(
            "https://api.anthropic.com/v1/messages",
            payload
        );

        var responseBody = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Claude response: {body}", responseBody);
        var json = JObject.Parse(responseBody);
        return json["content"]?[0]?["text"]?.ToString() ?? "Sorry, I couldn't process that.";
    }

    private void SendWhatsApp(string to, string message)
    {
        var accountSid = Environment.GetEnvironmentVariable("TwilioAccountSid");
        var authToken = Environment.GetEnvironmentVariable("TwilioAuthToken");
        var from = Environment.GetEnvironmentVariable("TwilioWhatsAppFrom");

        TwilioClient.Init(accountSid, authToken);

        MessageResource.Create(
            body: message,
            from: new Twilio.Types.PhoneNumber(from),
            to: new Twilio.Types.PhoneNumber($"whatsapp:+{to}")
        );

        _logger.LogInformation("Twilio message sent to {to}", to);
    }
}
