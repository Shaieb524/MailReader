using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace GmailApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GmailController : ControllerBase
{
    private readonly string _applicationName;
    private readonly string[] Scopes = { GmailService.Scope.GmailReadonly };
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private const string CredentialsPath = "token.json";
    private static readonly Dictionary<string, JsonDocument> _tokenStore = new Dictionary<string, JsonDocument>(); 


    public GmailController(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _applicationName = _configuration["GmailApi:ApplicationName"];
    }

    [HttpGet("authorize")]
    public IActionResult Authorize()
    {
        string clientId = _configuration["Authentication:Google:ClientId"];
        string redirectUri = Url.Action("HandleGoogleCallback", "Gmail", null, Request.Scheme, Request.Host.ToString()); //callback for the API.
        string authorizationUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
            $"client_id={clientId}&" +
            $"redirect_uri={redirectUri}&" +
            $"include_granted_scopes=true&" +
            $"state=state_parameter_passthrough_value&" +
            $"response_type=code&" +
            $"scope={string.Join(" ", Scopes)}&" +
            $"access_type=offline"; // offline for refresh token

        return Ok(new { authorizationUrl });
    }

    [HttpGet("HandleGoogleCallback")]
    public async Task<IActionResult> HandleGoogleCallback(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Authorization code is missing.");
        }

        try
        {
            var tokenResponse = await ExchangeCodeForTokens(code);
            var userId = Guid.NewGuid().ToString(); // Generate a user ID for in-memory storage.
            _tokenStore[userId] = tokenResponse; // Store the tokens in memory.
            return Ok(new { userId, tokenResponse }); // Return the user ID to the client.

        }
        catch (Exception ex)
        {
            return BadRequest($"Token exchange failed: {ex.Message}");
        }
    }

    private async Task<JsonDocument> ExchangeCodeForTokens(string code)
    {
        string clientId = _configuration["Authentication:Google:ClientId"];
        string clientSecret = _configuration["Authentication:Google:ClientSecret"];
        string redirectUri = Url.Action("HandleGoogleCallback", "Gmail", null, Request.Scheme, Request.Host.ToString());

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "code", code },
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "redirect_uri", redirectUri },
            { "grant_type", "authorization_code" }
        });

        var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    [HttpGet("emails")]
    //[Authorize]
    public async Task<IActionResult> GetEmails([FromQuery] string userId) 
    {
        if (string.IsNullOrEmpty(userId) || !_tokenStore.ContainsKey(userId))
        {
            return BadRequest("Invalid user ID.");
        }

        try
        {
            var tokenResponse = _tokenStore[userId];
            string accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();
            //string refreshToken = tokenResponse.RootElement.GetProperty("refresh_token").GetString();

            var credential = GoogleCredential.FromAccessToken(accessToken).CreateScoped(Scopes);

            var service = new GmailService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = _applicationName,
            });

            var request = service.Users.Messages.List("me");
            request.MaxResults = 5;
            var response = await request.ExecuteAsync();

            var emails = new List<EmailViewModel>();
            if (response.Messages != null)
            {
                foreach (var message in response.Messages)
                {
                    var email = await service.Users.Messages.Get("me", message.Id).ExecuteAsync();
                    emails.Add(new EmailViewModel
                    {
                        Id = email.Id,
                        Subject = email.Payload.Headers.FirstOrDefault(h => h.Name == "Subject")?.Value,
                        From = email.Payload.Headers.FirstOrDefault(h => h.Name == "From")?.Value,
                        Snippet = email.Snippet
                    });
                }
            }
            return Ok(emails);
        }
        catch (Exception ex)
        {
            return BadRequest($"An error occurred: {ex.Message}");
        }
    }

    private async Task<UserCredential> GetGoogleCredentialsAsync()
    {
        using (var stream = new FileStream(CredentialsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            var user = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                new ClientSecrets
                {
                    ClientId = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Authentication:Google:ClientId"],
                    ClientSecret = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Authentication:Google:ClientSecret"]
                },
                Scopes,
                user,
                CancellationToken.None,
                new FileDataStore(CredentialsPath, true));
        }
    }
}


public class EmailViewModel
{
    public string Id { get; set; }
    public string Subject { get; set; }
    public string From { get; set; }
    public string Snippet { get; set; }
}