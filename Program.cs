using MailReader.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// services DI
builder.Services.AddSingleton<ITokenStorageService, FileTokenStorageService>();

// Add Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth";
        options.LogoutPath = "/auth/logout";
        options.Cookie.Name = "EmailReaderAuth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpClient();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocal", builder =>
    {
        builder.WithOrigins("https://localhost:5001", "http://localhost:5000")
               .AllowAnyMethod()
               .AllowAnyHeader()
               .AllowCredentials();
    });
});

// Add static files support
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

app.UseHttpsRedirection();

app.UseCors("AllowLocal");

// Serve static files from the wwwroot folder
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();