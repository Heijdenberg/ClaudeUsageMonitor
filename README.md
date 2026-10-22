# ClaudeUsageMonitor

Visar hur mycket av Claudes femtimmarsgräns och veckogräns som är förbrukat, och vad varje körning av dina agentflöden kostade i tokens.

| Projekt | Vad det gör |
|---|---|
| `ClaudeUsage.StatusLine` | Claude Codes statusradskommando. Skriver ut statusraden, sparar gränserna och läser in tokenförbrukning från Claude Codes sessionsloggar till `%LOCALAPPDATA%\ClaudeUsage\claude-usage.db`. Startar, gör sitt på cirka 30 ms och avslutas. |
| `ClaudeUsage.Monitor` | WPF-app som läser databasen var 5:e sekund. **Gränser** visar gränserna som ringar, **Körningar** jämför körningar uppdelat per agent och modell. Den skriver ingenting. |
| `ClaudeUsage.Core` | Delad kod: databas, inläsning av loggar, sammanslagning, priser. |
| `ClaudeUsage.Core.Tests` | Enhetstester. |

## Vad som sparas

- **Gränser.** Claude Code skickar `rate_limits.five_hour` och `rate_limits.seven_day` till statusraden. Flera sessioner rapporterar samtidigt, och en session som står still fortsätter rapportera gamla värden. Därför slås värdena ihop: inom ett fönster kan procenten bara öka, och en senare återställningstid betyder ett nytt fönster. Varje ändring sparas i `limit_history`.
- **Tokens per svar**, från `%USERPROFILE%\.claude\projects\**\*.jsonl`: inmatning, utskrivet (varav tänkande), cacheläsning, cacheskrivning (5 min och 1 h), webbsökningar och modell. Ett svar som loggas på flera rader räknas en gång.
- **Körningar.** Varje meddelande du skickar har ett `promptId`, och allt det sätter igång bär samma id: underagenter, svar via `AskUserQuestion` och verktygssvar. En körning är ett meddelande plus eventuella notiser från bakgrundsagenter som följer, fram till ditt nästa meddelande. Underagenternas namn kommer från deras `.meta.json` (agenttyp och beskrivning), och sessionsnamn från `/rename`.
- **Etiketter för prompter:** första raden i varje meddelande du skickar, högst 120 tecken, så att körningar går att skilja åt. Inget annat ur konversationerna sparas.

Statusraden läser sin egen sessions logg vid varje körning och kontrollerar alla loggar högst en gång i minuten. Därför kommer även headless-körningar med `claude -p` med, så länge någon interaktiv session är öppen.

`usage.json` skrivs fortfarande också, i samma format som tidigare.

**API-pris** är vad tokens skulle kosta på Anthropics API, enligt listpriserna i `ClaudeUsage.Core/Pricing.cs`. Det är ett sätt att väga tokentyper och modeller mot varandra, inte vad prenumerationen kostar. Användning på claude.ai eller i skrivbordsappens chatt finns inte i loggarna.

## Bygga och installera

Kräver Windows, .NET 8 SDK och, för Native AOT-bygget, Visual Studios arbetsbelastning "Desktop development with C++".

```powershell
# AOT-kompilatorn i .NET 8 letar efter vswhere.exe i PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish ClaudeUsage.StatusLine -c Release -r win-x64 -o "$env:USERPROFILE\.claude\claude-usage"

# Läs in befintliga sessionsloggar en gång (tar några sekunder).
& "$env:USERPROFILE\.claude\claude-usage\ClaudeUsage.StatusLine.exe" --import-all

# Starta appen.
dotnet run --project ClaudeUsage.Monitor
```

Utan C++-arbetsbelastningen: publicera med `-p:PublishAot=false` i stället. Då krävs .NET 8-runtime, och programmet startar långsammare.

Peka sedan Claude Codes statusrad på exe-filen i `%USERPROFILE%\.claude\settings.json`. Ändra bara nyckeln `statusLine` och behåll alla andra inställningar:

```json
"statusLine": {
  "type": "command",
  "command": "C:/Users/<ANVÄNDARNAMN>/.claude/claude-usage/ClaudeUsage.StatusLine.exe",
  "refreshInterval": 5
}
```

För att uppdatera exe-filen senare publicerar du igen. Om en session råkar köra den just då misslyckas kopieringen, och då är det bara att köra igen.

## Testa

```powershell
dotnet test ClaudeUsage.Core.Tests
```

För att prova exe-filen utan att röra riktig data: peka `CLAUDE_USAGE_DIR` på en testmapp (både databasen och `usage.json` hamnar där), och `CLAUDE_CONFIG_DIR` på en tom mapp om loggarna ska hoppas över.

```powershell
$env:CLAUDE_USAGE_DIR = Join-Path $env:TEMP 'usage-test'
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
'{"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":' + ($now+8100) + '}}}' | & "$env:USERPROFILE\.claude\claude-usage\ClaudeUsage.StatusLine.exe"
# 5h: 42% (återställs om 2h 14m) | 7d: inväntar data
Remove-Item $env:CLAUDE_USAGE_DIR -Recurse -Force; Remove-Item Env:CLAUDE_USAGE_DIR
```

Om något går fel när data sparas skrivs statusraden ändå ut, och det senaste felet hamnar i `%LOCALAPPDATA%\ClaudeUsage\claude-usage-error.txt`.

## Filer

- `%LOCALAPPDATA%\ClaudeUsage\claude-usage.db`: SQLite (WAL). Ligger i din egen profil eftersom den innehåller etiketter från dina meddelanden, och i Local (inte Roaming) eftersom SQLite-filer inte tål att synkas. Tabellerna `limits`, `limit_history`, `responses`, `prompts`, `agents`, `sessions` och `log_files` (hur långt varje logg är inläst).
- `C:\temp\usage.json`: tidsstämpel, procent och återställningstid för båda fönstren, `null` när värden saknas.

```json
{
  "timestamp": "2026-10-22T11:37:33.1772476+02:00",
  "five_hour": { "used_percentage": 17, "resets_at": "2026-10-22T15:30:00.0000000+02:00" },
  "seven_day": { "used_percentage": 2, "resets_at": "2026-10-28T22:00:00.0000000+01:00" }
}
```

## Utan .NET: PowerShell-skriptet

`scripts/statusline.ps1` är den tidigare versionen. Den visar statusraden och skriver `usage.json`, men inte databasen. Kopiera den till `%USERPROFILE%\.claude\statusline.ps1` och behåll den sparad som UTF-8 med BOM, annars blir "å" och "ä" skräptecken i Windows PowerShell 5.1. Använd det här kommandot:

```json
"command": "powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/<ANVÄNDARNAMN>/.claude/statusline.ps1"
```

Skriptet startar PowerShell vid varje körning, cirka 0,5 s per gång.

## Bra att veta

- `rate_limits` finns bara för Pro- och Max-prenumeranter, och först efter sessionens första svar från API:et.
- Procenten ändras bara när Claude Code får ett svar från API:et. Ett svar räknas när det är klart, inte medan det skrivs.
