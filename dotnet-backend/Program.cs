using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var frontendOrigin = builder.Configuration.GetValue<string>("FrontendUrl") ?? "http://localhost:5173";
var rasaBaseUrl = builder.Configuration.GetValue<string>("Rasa:BaseUrl") ?? "http://localhost:5005";

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(frontendOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<DummyJwtService>();
builder.Services.AddHttpClient<RasaMessenger>(client =>
{
    client.BaseAddress = new Uri(rasaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapPost("/auth/login", (LoginRequest request, DummyJwtService jwt) =>
{
    if (string.IsNullOrWhiteSpace(request?.Username))
    {
        return Results.BadRequest(new { error = "Username is required." });
    }

    var username = request.Username.Trim();
    var token = jwt.CreateToken(username);
    return Results.Ok(new LoginResponse(token, username));
});

app.MapGet("/conversations", (HttpContext context, DummyJwtService jwt, ConversationStore store) =>
{
    var userId = TryGetUserId(context, jwt);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversations = store.ListSummaries(userId);
    return Results.Ok(conversations);
});

app.MapPost("/conversations", (CreateConversationRequest request, HttpContext context, DummyJwtService jwt, ConversationStore store) =>
{
    var userId = TryGetUserId(context, jwt);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversation = store.Create(userId, request?.Title);
    return Results.Created($"/conversations/{conversation.Id}", conversation.ToDetails());
});

app.MapGet("/conversations/{id:guid}", (Guid id, HttpContext context, DummyJwtService jwt, ConversationStore store) =>
{
    var userId = TryGetUserId(context, jwt);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var conversation = store.Get(userId, id);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(conversation.ToDetails());
});

app.MapPost("/conversations/{id:guid}/messages", async (Guid id, SendMessageRequest request, HttpContext context, DummyJwtService jwt, ConversationStore store, RasaMessenger rasa, CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(context, jwt);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request?.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    var conversation = store.Get(userId, id);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    var trimmedMessage = request.Message.Trim();
    var timestamp = DateTimeOffset.UtcNow;
    var userMessage = new ChatMessage("user", trimmedMessage, timestamp);
    conversation.AddMessage(userMessage);

    try
    {
        var botMessages = await rasa.SendMessageAsync(id, trimmedMessage, cancellationToken);
        foreach (var botMessage in botMessages)
        {
            conversation.AddMessage(botMessage);
        }
    }
    catch (RasaRequestException ex)
    {
        conversation.AddMessage(new ChatMessage("system", $"Rasa request failed: {ex.Message}", DateTimeOffset.UtcNow));
    }

    return Results.Ok(conversation.ToDetails());
});

app.Run();

static string? TryGetUserId(HttpContext context, DummyJwtService jwt)
{
    if (!context.Request.Headers.TryGetValue("Authorization", out var values))
    {
        return null;
    }

    var headerValue = values.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(headerValue))
    {
        return null;
    }

    if (!AuthenticationHeaderValue.TryParse(headerValue, out var header) ||
        !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(header.Parameter))
    {
        return null;
    }

    return jwt.TryGetSubject(header.Parameter);
}

record LoginRequest(string Username, string Password);

record LoginResponse(string Token, string DisplayName);

record CreateConversationRequest(string? Title);

record SendMessageRequest(string Message);

record ConversationSummary(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

record ConversationDetails(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChatMessage> Messages);

record ChatMessage(string Role, string Text, DateTimeOffset Timestamp);

class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Conversation>> _userConversations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ConversationSummary> ListSummaries(string userId)
    {
        if (!_userConversations.TryGetValue(userId, out var conversations))
        {
            return Array.Empty<ConversationSummary>();
        }

        return conversations.Values
            .Select(conversation => conversation.ToSummary())
            .OrderByDescending(summary => summary.UpdatedAt)
            .ToList();
    }

    public Conversation Create(string userId, string? title)
    {
        var conversations = _userConversations.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Conversation>());
        var conversation = new Conversation(title);
        conversations[conversation.Id] = conversation;
        return conversation;
    }

    public Conversation? Get(string userId, Guid id)
    {
        if (_userConversations.TryGetValue(userId, out var conversations) &&
            conversations.TryGetValue(id, out var conversation))
        {
            return conversation;
        }

        return null;
    }
}

class Conversation
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    public Conversation(string? title)
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim();
    }

    public Guid Id { get; }
    public string Title { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public ConversationSummary ToSummary()
    {
        lock (_lock)
        {
            return new ConversationSummary(Id, Title, CreatedAt, UpdatedAt);
        }
    }

    public ConversationDetails ToDetails()
    {
        lock (_lock)
        {
            return new ConversationDetails(
                Id,
                Title,
                CreatedAt,
                UpdatedAt,
                _messages.ToList());
        }
    }

    public void AddMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            UpdatedAt = message.Timestamp;

            if (_messages.Count == 1 && message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                Title = BuildTitleFromMessage(message.Text);
            }
        }
    }

    private static string BuildTitleFromMessage(string text)
    {
        var fallback = "Conversation";
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var cleaned = text.Trim();
        return cleaned.Length <= 40 ? cleaned : $"{cleaned[..40]}...";
    }
}

class DummyJwtService
{
    public string CreateToken(string username)
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            sub = username,
            name = username,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));
        return $"{header}.{payload}.";
    }

    public string? TryGetSubject(string token)
    {
        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadJson = Base64UrlDecode(segments[1]);
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.TryGetProperty("sub", out var subElement))
            {
                var value = subElement.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string Base64UrlEncode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+')
                          .Replace('_', '/');

        var padding = 4 - padded.Length % 4;
        if (padding is > 0 and < 4)
        {
            padded = padded.PadRight(padded.Length + padding, '=');
        }

        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}

class RasaMessenger
{
    private readonly HttpClient _httpClient;

    public RasaMessenger(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<ChatMessage>> SendMessageAsync(Guid conversationId, string message, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/webhooks/rest/webhook",
                new { sender = conversationId.ToString(), message },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new RasaRequestException($"Rasa responded with {(int)response.StatusCode}: {content}");
            }

            var rasaMessages = await response.Content.ReadFromJsonAsync<List<RasaResponse>>(cancellationToken: cancellationToken)
                ?? new List<RasaResponse>();

            return rasaMessages
                .Where(rasaMessage => !string.IsNullOrWhiteSpace(rasaMessage.Text))
                .Select(rasaMessage => new ChatMessage("bot", rasaMessage.Text!.Trim(), DateTimeOffset.UtcNow))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new RasaRequestException("Unable to reach the Rasa server.", ex);
        }
    }

    private sealed record RasaResponse(string? Recipient_id, string? Text);
}

class RasaRequestException : Exception
{
    public RasaRequestException(string message)
        : base(message)
    {
    }

    public RasaRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
