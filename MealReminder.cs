using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MealReminderFunction;

public class MealReminder
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public MealReminder(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<MealReminder>();
        _http = new HttpClient();
    }

    [Function("MealReminder")]
    // public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo myTimer)
    public async Task Run([TimerTrigger("0 0 8,12,17 * * *")] TimerInfo myTimer)
    {
        // 1. Read today's meals
        var basePath = AppContext.BaseDirectory;
        var json = File.ReadAllText(Path.Combine(basePath, "meals.json"));
        var plan = JsonConvert.DeserializeObject<MealPlan>(json)!;
        var today = DateTime.Now.DayOfWeek.ToString();

        if (!plan.WeeklyPlan.TryGetValue(today, out var meals))
        {
            _logger.LogInformation("No meals for {day}", today);
            return;
        }

        // 2. Build the message
        var remaining = plan.DailyTarget - meals.Calories;
        var message = $"""
            Good morning! Here's your plan for today 🍱

            Lunch: {meals.Lunch}
            Dinner: {meals.Dinner}

            Batched meals: {meals.Calories} kcal | {meals.Protein}g protein
            Remaining for breakfast + snacks: {remaining} kcal
            Daily target: {plan.DailyTarget} kcal

            You've got this 💪
            """;

        // 3. Send via WhatsApp
        var token = Environment.GetEnvironmentVariable("WhatsAppToken");
        var phoneId = Environment.GetEnvironmentVariable("PhoneNumberId");
        var yourNumber = Environment.GetEnvironmentVariable("YourWhatsAppNumber");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var payload = new
        {
            messaging_product = "whatsapp",
            to = yourNumber,
            type = "text",
            text = new { body = message }
        };

        var response = await _http.PostAsJsonAsync(
            $"https://graph.facebook.com/v20.0/{phoneId}/messages",
            payload);

        var responseBody = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("WhatsApp response: {status} - {body}", response.StatusCode, responseBody);
    }
}

// Models
public class MealPlan
{
    public Dictionary<string, DayMeals> WeeklyPlan { get; set; } = new();
    public int DailyTarget { get; set; }
}

public class DayMeals
{
    public string Lunch { get; set; } = "";
    public string Dinner { get; set; } = "";
    public int Calories { get; set; }
    public int Protein { get; set; }
}