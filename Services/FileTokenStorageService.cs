using MailReader.Services;
using System.Text.Json;

namespace MailReader.Services;


/*
this should be DB
*/

public class FileTokenStorageService : ITokenStorageService
{
    private readonly string _tokenStoragePath;
    private readonly string _mappingStoragePath;
    private readonly ILogger<FileTokenStorageService> _logger;

    public FileTokenStorageService(IConfiguration configuration, ILogger<FileTokenStorageService> logger)
    {
        string basePath = configuration["TokenStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");
        _tokenStoragePath = Path.Combine(basePath, "tokens");
        _mappingStoragePath = Path.Combine(basePath, "mappings");
        _logger = logger;

        // Ensure directories exist
        EnsureDirectoryExists(_tokenStoragePath);
        EnsureDirectoryExists(_mappingStoragePath);
    }

    private void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public async Task<JsonDocument> GetTokenAsync(string userId)
    {
        string filePath = GetTokenFilePath(userId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading token for user {UserId}", userId);
            return null;
        }
    }

    public async Task SaveTokenAsync(string userId, JsonDocument token)
    {
        string filePath = GetTokenFilePath(userId);

        try
        {
            // Create a more complete token object with creation time if not present
            var tokenJson = token.RootElement.ToString();
            var tokenObj = JsonSerializer.Deserialize<Dictionary<string, object>>(tokenJson);

            if (!tokenObj.ContainsKey("created"))
            {
                tokenObj["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                tokenJson = JsonSerializer.Serialize(tokenObj);
            }

            await File.WriteAllTextAsync(filePath, tokenJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving token for user {UserId}", userId);
            throw;
        }
    }

    public Task RemoveTokenAsync(string userId)
    {
        string filePath = GetTokenFilePath(userId);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    public async Task<JsonDocument> GetUserMappingAsync(string userId)
    {
        string filePath = GetMappingFilePath(userId);

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading mapping for user {UserId}", userId);
            return null;
        }
    }

    public async Task SaveUserMappingAsync(string userId, JsonDocument mappingData)
    {
        string filePath = GetMappingFilePath(userId);

        try
        {
            string mappingJson = mappingData.RootElement.ToString();
            await File.WriteAllTextAsync(filePath, mappingJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving mapping for user {UserId}", userId);
            throw;
        }
    }

    public async Task<List<string>> GetAllUserIdsAsync()
    {
        try
        {
            // Get all mapping files
            var files = Directory.GetFiles(_mappingStoragePath, "*.json");
            var userIds = new List<string>();

            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);

                // Verify that this user also has a token file
                if (File.Exists(GetTokenFilePath(fileName)))
                {
                    userIds.Add(fileName);
                }
            }

            return userIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all user IDs");
            return new List<string>();
        }
    }

    private string GetTokenFilePath(string userId)
    {
        // Sanitize the userId to prevent directory traversal attacks
        // by only accepting letters, digits, and hyphens (as uuids may contain them)
        string sanitizedUserId = new string(userId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return Path.Combine(_tokenStoragePath, $"{sanitizedUserId}.json");
    }

    private string GetMappingFilePath(string userId)
    {
        // Sanitize the userId to prevent directory traversal attacks
        // by only accepting letters, digits, and hyphens (as uuids may contain them)
        string sanitizedUserId = new string(userId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return Path.Combine(_mappingStoragePath, $"{sanitizedUserId}.json");
    }
}