# UsageMonitor

En liten WPF-app som visar Claude-förbrukningen för femtimmarsgränsen och veckogränsen.

Appen läser `C:\temp\usage.json` var 5:e sekund. Filen skrivs av Claude Codes statusradsskript (`%USERPROFILE%\.claude\statusline.ps1`) med `refreshInterval: 5`.

## Kör

```powershell
dotnet run
```

Kräver .NET 8 SDK och Windows.

## Filformat

```json
{
    "timestamp":  "2026-10-05T21:01:33.3969092+02:00",
    "five_hour":  { "used_percentage": 2, "resets_at": "2026-10-06T01:30:00.0000000+02:00" },
    "seven_day":  { "used_percentage": 21, "resets_at": "2026-10-07T23:00:00.0000000+02:00" }
}
```

Värden som saknas är `null` och visas som "inväntar data". Är filen äldre än 30 sekunder visar appen en varning om att den inte uppdateras.
