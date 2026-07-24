# ChizuChan configuration

ChizuChan keeps non-secret settings in `appsettings.json` and credentials outside the repository.

## Development

1. Copy `appsettings.example.json` to `appsettings.json`.
2. Store credentials with the existing .NET User Secrets ID:

```powershell
dotnet user-secrets set "Discord:Token" "<bot-token>"
dotnet user-secrets set "ApiKeys:OverseerrKey" "<api-key>"
dotnet user-secrets set "ApiKeys:SonarrAnimeKey" "<api-key>"
dotnet user-secrets set "ApiKeys:SonarrTvKey" "<api-key>"
dotnet user-secrets set "ApiKeys:RadarrAnimeKey" "<api-key>"
dotnet user-secrets set "ApiKeys:RadarrMovieKey" "<api-key>"
dotnet user-secrets set "Lidarr:ApiKey" "<api-key>"
```

`appsettings.json`, environment-specific appsettings files, and `secrets.json` are ignored by Git.

## Production

On Windows, ChizuChan automatically loads:

```text
C:\ProgramData\ChizuChan\secrets.json
```

The file contains only secret overrides:

```json
{
  "Discord": {
    "Token": "<bot-token>"
  },
  "ApiKeys": {
    "OverseerrKey": "<api-key>",
    "SonarrAnimeKey": "<api-key>",
    "SonarrTvKey": "<api-key>",
    "RadarrAnimeKey": "<api-key>",
    "RadarrMovieKey": "<api-key>"
  },
  "Lidarr": {
    "ApiKey": "<api-key>"
  }
}
```

Restrict this file to the account running ChizuChan, local Administrators, and SYSTEM.

To use another location, set `CHIZUCHAN_SECRETS_PATH` to an absolute JSON file path. An explicitly configured path is fail-closed: ChizuChan will refuse to start if the file is missing.

Never place credentials in `appsettings.example.json`, deployment folders, documentation, commits, issues, or pull requests.
