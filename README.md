# TokenUsageMonitor

Visar hur mycket av Claudes femtimmarsgräns och veckogräns som är förbrukat i Claude Code, och vad varje körning av dina agentflöden kostade i tokens.

Ett fristående hobbyprojekt. Det är inte kopplat till, godkänt av eller sponsrat av Anthropic. Claude och Claude Code är Anthropics varumärken.

| Projekt | Vad det gör |
|---|---|
| `TokenUsage.StatusLine` | Claude Codes statusradskommando. Skriver ut statusraden, sparar gränserna och läser in tokenförbrukning från Claude Codes sessionsloggar till `%LOCALAPPDATA%\TokenUsage\token-usage.db`. Startar, gör sitt på cirka 30 ms och avslutas. |
| `TokenUsage.Monitor` | WPF-app som läser databasen var 5:e sekund. **Gränser** visar gränserna som ringar, **Körningar** jämför körningar uppdelat per agent och modell. Den skriver ingenting. |
| `TokenUsage.Core` | Delad kod: databas, inläsning av loggar, sammanslagning, priser. |
| `TokenUsage.Core.Tests` | Enhetstester. |

## Vad som sparas

- **Gränser.** Claude Code skickar `rate_limits.five_hour` och `rate_limits.seven_day` till statusraden. Flera sessioner rapporterar samtidigt, och en session som står still fortsätter rapportera gamla värden. Därför slås värdena ihop: inom ett fönster kan procenten bara öka, och en senare återställningstid betyder ett nytt fönster. Varje ändring sparas i `limit_history`.
- **Tokens per svar**, från `%USERPROFILE%\.claude\projects\**\*.jsonl`: inmatning, utskrivet (varav tänkande), cacheläsning, cacheskrivning (5 min och 1 h), webbsökningar och modell. Ett svar som loggas på flera rader räknas en gång.
- **Körningar.** Varje meddelande du skickar har ett `promptId`, och allt det sätter igång bär samma id: underagenter, svar via `AskUserQuestion` och verktygssvar. En körning är ett meddelande plus eventuella notiser från bakgrundsagenter som följer, fram till ditt nästa meddelande. Underagenternas namn kommer från deras `.meta.json` (agenttyp och beskrivning), som läses igen när den ändras, även om agentens logg har slutat växa.
- **Sessionsnamn.** Ditt eget namn från `/rename` i första hand, annars namnet som Claude Code genererar automatiskt (`ai-title` i loggen; det kan ändras medan sessionen pågår). Session-id:t ändras aldrig och visas därför i en egen kolumn.
- **Ingen text ur dina meddelanden.** Ett meddelande kan innehålla hemligheter, till exempel en nyckel, så databasen sparar bara var meddelandet finns i Claude Codes logg (fil och position). Appen läser första raden därifrån när den visas. För slash-kommandon sparas kommandonamnet, till exempel `/workflow`, men aldrig det som står efter. När Claude Code har rensat bort en logg (som standard efter 30 dagar) visas bara kommandonamnet, eller "(texten finns inte längre i loggen)".

Statusraden läser sin egen sessions logg vid varje körning och kontrollerar alla loggar högst en gång i minuten. Därför kommer även headless-körningar med `claude -p` med, så länge någon interaktiv session är öppen.

**API-pris** är vad tokens skulle kosta på Anthropics API, enligt listpriserna i `TokenUsage.Core/Pricing.cs`. Det är ett sätt att väga tokentyper och modeller mot varandra, inte vad prenumerationen kostar. Användning på claude.ai eller i skrivbordsappens chatt finns inte i loggarna.

## Använda appen

**Gränser**
- En ring per fönster, med procent, när fönstret återställs och tid kvar. Färgen blir gul från 75 % och röd från 90 %.
- Under varje ring: antal svar, utskrivna tokens och API-pris i fönstret, enligt Claude Codes loggar.
- När återställningstiden har passerat visas ingen procent. Ringen visar "inväntar data" tills en session rapporterar nya värden, och summeringen gäller förra fönstret.
- Längst ner: när en session senast rapporterade och när procenten senast ändrades. Har ingen session rapporterat på 30 sekunder visas en varning.

**Körningar**
- Välj **tidsperiod** (24 timmar, 7 dagar, 30 dagar eller allt) och **session** (alla, eller en enskild med namn och id). Filtren kombineras, och sammanfattningen överst räknar bara det som visas. En körning hör till perioden den startade i och visas alltid hel, även svar som kom efter periodens slut. En körning som startade före perioden visas inte.
- Varje rad är en körning: start (när du skickade meddelandet), sessionens namn och id, första raden i ditt meddelande, tid från meddelandet till sista svaret, antal svar, tokens och API-pris. Klicka på en kolumnrubrik för att sortera.
- Högerklicka på en rad för att kopiera hela raden (även Ctrl+C) eller bara cellen, eller för att visa bara den sessionen. Prompten och sessions-id:t kopieras i sin helhet. Prompten läses då från Claude Codes logg och hamnar i urklipp, och därmed i Windows urklippshistorik om den är påslagen.
- Välj en körning för att se den uppdelad per agent och modell: huvudsessionen och varje underagent för sig, med inmatning, utskrivet, tänkande, cacheläsning, cacheskrivning och API-pris.
- Listan uppdateras var 30:e sekund medan fliken är öppen, eller direkt med **Uppdatera**. Valt filter, vald rad och sortering behålls.

## Bygga och installera

Kräver Windows, .NET 8 SDK och, för Native AOT-bygget, Visual Studios arbetsbelastning "Desktop development with C++".

```powershell
# AOT-kompilatorn i .NET 8 letar efter vswhere.exe i PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish TokenUsage.StatusLine -c Release -r win-x64 -o "$env:USERPROFILE\.claude\token-usage"

# Läs in befintliga sessionsloggar en gång (tar några sekunder).
& "$env:USERPROFILE\.claude\token-usage\TokenUsage.StatusLine.exe" --import-all

# Starta appen.
dotnet run --project TokenUsage.Monitor
```

Utan C++-arbetsbelastningen: publicera med `-p:PublishAot=false` i stället. Då krävs .NET 8-runtime, och programmet startar långsammare.

Peka sedan Claude Codes statusrad på exe-filen i `%USERPROFILE%\.claude\settings.json`. Ändra bara nyckeln `statusLine` och behåll alla andra inställningar:

```json
"statusLine": {
  "type": "command",
  "command": "C:/Users/<ANVÄNDARNAMN>/.claude/token-usage/TokenUsage.StatusLine.exe",
  "refreshInterval": 5
}
```

För att uppdatera exe-filen senare publicerar du igen. Om en session råkar köra den just då misslyckas kopieringen, och då är det bara att köra igen.

## Testa

```powershell
dotnet test TokenUsage.Core.Tests
```

För att prova exe-filen utan att röra riktig data: peka `TOKEN_USAGE_DIR` på en testmapp, och `CLAUDE_CONFIG_DIR` på en tom mapp om loggarna ska hoppas över.

```powershell
$env:TOKEN_USAGE_DIR = Join-Path $env:TEMP 'usage-test'
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
'{"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":' + ($now+8100) + '}}}' | & "$env:USERPROFILE\.claude\token-usage\TokenUsage.StatusLine.exe"
# 5h: 42% (återställs om 2h 14m) | 7d: inväntar data
Remove-Item $env:TOKEN_USAGE_DIR -Recurse -Force; Remove-Item Env:TOKEN_USAGE_DIR
```

Om något går fel när data sparas skrivs statusraden ändå ut, och det senaste felet hamnar i `%LOCALAPPDATA%\TokenUsage\token-usage-error.txt`.

## Databasen

`%LOCALAPPDATA%\TokenUsage\token-usage.db`, SQLite i WAL-läge. Den ligger i din egen profil, eftersom sessionsnamn och agentbeskrivningar säger en del om vad du arbetar med, och i Local (inte Roaming) eftersom SQLite-filer inte tål att synkas.

| Tabell | Innehåll |
|---|---|
| `limits` | Aktuella värden per fönster, med när de senast rapporterades och ändrades. |
| `limit_history` | En rad varje gång procenten eller återställningstiden ändras. |
| `responses` | Ett svar per rad: session, prompt, underagent, modell och tokens per typ. |
| `prompts` | Dina meddelanden: id, session, tid, ursprung (du eller en bakgrundsagent), kommandonamn och position i loggen. Ingen text. |
| `agents` | Underagenter: typ, beskrivning och modell. |
| `sessions` | Projekt, ditt eget namn (`title`) och Claude Codes namn (`ai_title`). |
| `log_files` | Hur långt varje logg är inläst. |

Databasen uppgraderas automatiskt när en ny version av statusradsprogrammet öppnar den. Behöver loggarna läsas in på nytt görs det då, utan att svar räknas dubbelt.

## Bra att veta

- Gränserna finns bara med en Pro- eller Max-prenumeration. Använder du Claude Code med en API-nyckel finns inga gränser att visa: statusraden och ringarna visar "inväntar data", men fliken Körningar fungerar som vanligt eftersom tokendatan kommer från sessionsloggarna. Med en prenumeration dyker värdena upp efter sessionens första svar.
- Procenten ändras bara när Claude Code får ett svar från API:et. Ett svar räknas när det är klart, inte medan det skrivs.

## Licens

MIT, se [LICENSE](LICENSE).
