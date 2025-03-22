using System.Text.Json;

namespace MailReader.Services;

public interface ITokenStorageService
{
    Task<JsonDocument> GetTokenAsync(string userId);
    Task SaveTokenAsync(string userId, JsonDocument token);
    Task RemoveTokenAsync(string userId);

    // User mapping methods
    Task<JsonDocument> GetUserMappingAsync(string userId);
    Task SaveUserMappingAsync(string userId, JsonDocument mappingData);
    Task<List<string>> GetAllUserIdsAsync();
}