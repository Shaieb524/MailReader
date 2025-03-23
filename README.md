# MailReader

A .NET Core web application that integrates with Gmail API to read and process emails.

## Overview

MailReader provides a web-based interface and API to access Gmail emails through OAuth authentication. It allows you to interact with your Gmail account programmatically, retrieve emails, and perform various operations on them.

## Features

- Gmail API integration
- OAuth authentication
- Token storage for maintaining sessions
- RESTful API for email operations
- CORS support for local development

## Prerequisites

- .NET 8.0 SDK
- Google Cloud Platform account with Gmail API enabled
- OAuth 2.0 client credentials for Gmail API

## Setup

1. Clone the repository
2. Configure the application

```bash
git clone https://github.com/yourusername/MailReader.git
cd MailReader
```

3. Update `appsettings.json` with your OAuth credentials

## Configuration

Update the `appsettings.json` file with your Gmail API credentials and other configuration options:

```json
{
  "GmailApi": {
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "RedirectUri": "https://localhost:5001/auth/callback"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

## Running the Application

```bash
dotnet run
```

The application will be available at:
- HTTP: http://localhost:5000
- HTTPS: https://localhost:5001

## API Endpoints

The application provides RESTful API endpoints through the GmailController.cs to interact with Gmail.

## Authentication

The application uses cookie-based authentication. Upon successful OAuth authentication with Gmail, a cookie named "EmailReaderAuth" is created.

## Roadmap

- Add user management without cloud registration
- Enhance security measures
- Migrate token storage from file-based to database
- Consider JWT authentication instead of cookies

## License

[MIT License](LICENSE)

## Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.