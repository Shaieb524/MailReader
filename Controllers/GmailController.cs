using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using MailReader.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GmailController : ControllerBase
{
    private readonly string _applicationName;
    private readonly string[] _scopes = { GmailService.Scope.GmailReadonly };
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ITokenStorageService _tokenStorage;

    public GmailController(
        IConfiguration configuration,
        HttpClient httpClient,
        ITokenStorageService tokenStorage)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _tokenStorage = tokenStorage;
        _applicationName = _configuration["GmailApi:ApplicationName"] ?? "Email Reader App";
    }

    [HttpGet("authorize")]
    public IActionResult Authorize([FromQuery] bool ongoingAccess = false)
    {
        string clientId = _configuration["Authentication:Google:ClientId"];
        string redirectUri = Url.Action("HandleCallback", "Gmail", new { ongoingAccess }, Request.Scheme, Request.Host.ToString());

        // Create state parameter to pass ongoingAccess preference
        var stateParams = new { OngoingAccess = ongoingAccess };
        string stateJson = JsonSerializer.Serialize(stateParams);
        string stateBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stateJson));

        string authorizationUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
            $"client_id={clientId}&" +
            $"redirect_uri={redirectUri}&" +
            $"include_granted_scopes=true&" +
            $"state={stateBase64}&" +
            $"response_type=code&" +
            $"scope={string.Join(" ", _scopes)}&" +
            $"access_type=offline&" + // offline for refresh token
            $"prompt=consent"; // Force consent screen to show every time

        return Redirect(authorizationUrl);
    }

    [HttpGet("HandleCallback")]
    public async Task<IActionResult> HandleCallback([FromQuery] string code, [FromQuery] string state)
    {
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Authorization code is missing.");
        }

        try
        {
            // Decode state parameter
            bool ongoingAccess = false;
            if (!string.IsNullOrEmpty(state))
            {
                try
                {
                    byte[] stateBytes = Convert.FromBase64String(state);
                    string stateJson = System.Text.Encoding.UTF8.GetString(stateBytes);
                    var stateParams = JsonSerializer.Deserialize<JsonElement>(stateJson);

                    if (stateParams.TryGetProperty("OngoingAccess", out JsonElement ongoingAccessElement))
                    {
                        ongoingAccess = ongoingAccessElement.GetBoolean();
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but continue
                    Console.WriteLine($"Error decoding state parameter: {ex.Message}");
                }
            }

            var tokenResponse = await ExchangeCodeForTokens(code);
            string userId = Guid.NewGuid().ToString();

            // Add metadata to token about access preferences
            var tokenData = JsonDocument.Parse(tokenResponse.RootElement.GetRawText());
            var tokenObj = new Dictionary<string, object>();

            foreach (JsonProperty property in tokenData.RootElement.EnumerateObject())
            {
                tokenObj[property.Name] = property.Value.Clone();
            }

            // Add additional metadata
            tokenObj["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            tokenObj["ongoingAccess"] = ongoingAccess;

            if (!ongoingAccess)
            {
                // Set expiration date 6 months from now
                tokenObj["expirationDate"] = DateTimeOffset.UtcNow.AddMonths(6).ToUnixTimeSeconds();
            }

            var updatedTokenJson = JsonSerializer.Serialize(tokenObj);
            var updatedToken = JsonDocument.Parse(updatedTokenJson);

            // Store the tokens securely
            await _tokenStorage.SaveTokenAsync(userId, updatedToken);

            // Get user email from Google
            string accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();
            var userEmail = await GetUserEmail(accessToken);

            // Save user mapping
            await SaveUserMapping(userId, userEmail);

            // Redirect to success page
            return Redirect($"/auth-success.html?userId={userId}&email={Uri.EscapeDataString(userEmail)}&ongoing={ongoingAccess}");
        }
        catch (Exception ex)
        {
            return BadRequest($"Token exchange failed: {ex.Message}");
        }
    }

    [HttpGet("emails")]
    public async Task<IActionResult> GetEmails([FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest("User ID is required.");
        }

        try
        {
            // Retrieve the tokens for the specified user
            var tokenResponse = await _tokenStorage.GetTokenAsync(userId);

            if (tokenResponse == null)
            {
                return BadRequest("No authorization tokens found for this user. Please authorize first.");
            }

            // Check if access has expired
            if (tokenResponse.RootElement.TryGetProperty("ongoingAccess", out var ongoingAccessProperty) &&
                !ongoingAccessProperty.GetBoolean())
            {
                if (tokenResponse.RootElement.TryGetProperty("expirationDate", out var expirationDateProperty))
                {
                    long expirationTimestamp = expirationDateProperty.GetInt64();
                    long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    if (currentTimestamp > expirationTimestamp)
                    {
                        return BadRequest("Access to this user's Gmail has expired. Please request authorization again.");
                    }
                }
            }

            string accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();

            // TODO make this function
            // Check if the token has expired
            if (IsTokenExpired(tokenResponse))
            {
                // Refresh the token if it has a refresh token
                if (tokenResponse.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement))
                {
                    string refreshToken = refreshTokenElement.GetString();
                    tokenResponse = await RefreshAccessToken(refreshToken);

                    // Preserve metadata when updating the token
                    var tokenObj = new Dictionary<string, object>();
                    foreach (JsonProperty property in tokenResponse.RootElement.EnumerateObject())
                    {
                        tokenObj[property.Name] = property.Value.Clone();
                    }

                    if (tokenResponse.RootElement.TryGetProperty("ongoingAccess", out var ongoingAccess))
                    {
                        tokenObj["ongoingAccess"] = ongoingAccess.GetBoolean();
                    }

                    if (tokenResponse.RootElement.TryGetProperty("expirationDate", out var expirationDate))
                    {
                        tokenObj["expirationDate"] = expirationDate.GetInt64();
                    }

                    tokenObj["refresh_token"] = refreshToken;

                    var updatedTokenJson = JsonSerializer.Serialize(tokenObj);
                    tokenResponse = JsonDocument.Parse(updatedTokenJson);

                    await _tokenStorage.SaveTokenAsync(userId, tokenResponse);
                    accessToken = tokenResponse.RootElement.GetProperty("access_token").GetString();
                }
                else
                {
                    return BadRequest("Access token has expired and no refresh token is available.");
                }
            }

            // TODO make this function
            var credential = GoogleCredential.FromAccessToken(accessToken).CreateScoped(_scopes);

            var service = new GmailService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = _applicationName,
            });

            var request = service.Users.Messages.List("me");
            request.MaxResults = 10;
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
                        Date = email.Payload.Headers.FirstOrDefault(h => h.Name == "Date")?.Value,
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

    [HttpGet("user")]
    public async Task<IActionResult> GetUserInfo([FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return BadRequest("User ID is required.");
        }

        try
        {
            // Get user email from mapping
            var userEmail = await GetUserEmailFromMapping(userId);
            if (string.IsNullOrEmpty(userEmail))
            {
                return NotFound("User not found.");
            }

            // Get token data
            var tokenResponse = await _tokenStorage.GetTokenAsync(userId);
            if (tokenResponse == null)
            {
                return BadRequest("No authorization data found for this user.");
            }

            // Extract access information
            bool ongoingAccess = false;
            string expiresOn = null;

            if (tokenResponse.RootElement.TryGetProperty("ongoingAccess", out var ongoingAccessElement))
            {
                ongoingAccess = ongoingAccessElement.GetBoolean();
            }

            if (!ongoingAccess && tokenResponse.RootElement.TryGetProperty("expirationDate", out var expirationDateElement))
            {
                long expirationTimestamp = expirationDateElement.GetInt64();
                expiresOn = DateTimeOffset.FromUnixTimeSeconds(expirationTimestamp).ToString("MMMM d, yyyy");
            }

            // TODO make this model
            return Ok(new
            {
                userId,
                email = userEmail,
                ongoingAccess,
                expiresOn
            });
        }
        catch (Exception ex)
        {
            return BadRequest($"An error occurred: {ex.Message}");
        }
    }

    // This method retrieves the authenticated user's email address from Google using their access token
    private async Task<string> GetUserEmail(string accessToken)
    {
        // create google creds obj for api calls & init Gmail API service 
        var credential = GoogleCredential.FromAccessToken(accessToken).CreateScoped(_scopes);

        var service = new GmailService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = _applicationName,
        });

        var profile = await service.Users.GetProfile("me").ExecuteAsync();
        return profile.EmailAddress;
    }

    private Task SaveUserMapping(string userId, string userEmail)
    {
        var mappingData = JsonDocument.Parse(JsonSerializer.Serialize(new { email = userEmail }));
        return _tokenStorage.SaveUserMappingAsync(userId, mappingData);
    }

    private async Task<string> GetUserEmailFromMapping(string userId)
    {
        var mapping = await _tokenStorage.GetUserMappingAsync(userId);
        if (mapping == null)
        {
            return null;
        }

        return mapping.RootElement.GetProperty("email").GetString();
    }

    // exchange token returned from google auth server with access token
    private async Task<JsonDocument> ExchangeCodeForTokens(string code)
    {
        try
        {
            string clientId = _configuration["Authentication:Google:ClientId"];
            string clientSecret = _configuration["Authentication:Google:ClientSecret"];

            // Generate the same redirect URI that was used in the authorize step
            string redirectUri = Url.Action("HandleCallback", "Gmail", new { ongoingAccess = false }, Request.Scheme, Request.Host.ToString());

            Console.WriteLine($"Exchanging code for tokens with redirect URI: {redirectUri}");

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "code", code },
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "redirect_uri", redirectUri },
                { "grant_type", "authorization_code" }
            });

            var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);

            string responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Token exchange failed with status code: {response.StatusCode}");
                Console.WriteLine($"Response content: {responseContent}");
                Console.WriteLine($"Request details: client_id={clientId}, redirect_uri={redirectUri}");

                throw new Exception($"Token exchange failed: {responseContent}");
            }

            return JsonDocument.Parse(responseContent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception during token exchange: {ex.Message}");
            throw;
        }
    }

    private bool IsTokenExpired(JsonDocument tokenResponse)
    {
        if (tokenResponse.RootElement.TryGetProperty("expires_in", out var expiresInElement) &&
            tokenResponse.RootElement.TryGetProperty("created", out var createdElement))
        {
            int expiresIn = expiresInElement.GetInt32();
            long created = createdElement.GetInt64();
            long expireTime = created + expiresIn;
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            return currentTime >= expireTime;
        }

        // If we can't determine, assume it's not expired
        return false;
    }

    private async Task<JsonDocument> RefreshAccessToken(string refreshToken)
    {
        string clientId = _configuration["Authentication:Google:ClientId"];
        string clientSecret = _configuration["Authentication:Google:ClientSecret"];

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "refresh_token", refreshToken },
            { "grant_type", "refresh_token" }
        });

        var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonDocument.Parse(json);

        // Add creation timestamp
        var jsonObject = new Dictionary<string, object>
        {
            { "access_token", tokenResponse.RootElement.GetProperty("access_token").GetString() },
            { "expires_in", tokenResponse.RootElement.GetProperty("expires_in").GetInt32() },
            { "created", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            { "refresh_token", refreshToken }
        };

        return JsonDocument.Parse(JsonSerializer.Serialize(jsonObject));
    }
}

public class EmailViewModel
{
    public string Id { get; set; }
    public string Subject { get; set; }
    public string From { get; set; }
    public string Date { get; set; }
    public string Snippet { get; set; }
}