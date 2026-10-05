# UsageMonitor

En liten WPF-app som visar Claude-förbrukningen för femtimmarsgränsen och veckogränsen.

Appen läser `C:\temp\usage.json` var 5:e sekund. Filen skrivs av Claude Codes statusradsskript (`%USERPROFILE%\.claude\statusline.ps1`) med `refreshInterval: 5`. Se [Sätta upp statusraden](#sätta-upp-statusraden) för hur det skapas.

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

## Sätta upp statusraden

Klistra in prompten nedan i Claude Code på en Windows-dator. Den skapar statusradsskriptet och inställningen som skriver `C:\temp\usage.json`.

````markdown
Sätt upp min Claude Code-statusrad på Windows så att den visar förbrukning för femtimmarsgränsen och veckogränsen och samtidigt sparar värdena till `C:\temp\usage.json`. Gör exakt så här:

## 1. Skapa skriptet

Skapa filen `%USERPROFILE%\.claude\statusline.ps1` med exakt detta innehåll:

```powershell
$raw = [Console]::In.ReadToEnd()
try { $d = $raw | ConvertFrom-Json } catch { $d = $null }

function Format-Window($label, $w) {
    if ($null -eq $w -or $null -eq $w.used_percentage) { return "${label}: inväntar data" }
    $pct = [math]::Round([double]$w.used_percentage)
    $out = "${label}: ${pct}%"
    if ($null -ne $w.resets_at) {
        $reset = [DateTimeOffset]::FromUnixTimeSeconds([int64]$w.resets_at)
        $span = $reset - [DateTimeOffset]::UtcNow
        if ($span.TotalSeconds -lt 0) { $span = [TimeSpan]::Zero }
        if ($span.TotalDays -ge 1) {
            $t = '{0}d {1}h' -f [int][math]::Floor($span.TotalDays), $span.Hours
        } elseif ($span.TotalHours -ge 1) {
            $t = '{0}h {1}m' -f [int][math]::Floor($span.TotalHours), $span.Minutes
        } else {
            $t = '{0}m' -f [int][math]::Ceiling($span.TotalMinutes)
        }
        $out += " (återställs om $t)"
    } else {
        $out += " (återställning: inväntar data)"
    }
    return $out
}

$rl = if ($d) { $d.rate_limits } else { $null }
$five = Format-Window '5h' $(if ($rl) { $rl.five_hour })
$week = Format-Window '7d' $(if ($rl) { $rl.seven_day })
function Get-UsageEntry($w) {
    $pct = $null; $reset = $null
    if ($null -ne $w) {
        if ($null -ne $w.used_percentage) { $pct = [double]$w.used_percentage }
        if ($null -ne $w.resets_at) {
            $reset = [DateTimeOffset]::FromUnixTimeSeconds([int64]$w.resets_at).ToLocalTime().ToString('o')
        }
    }
    return [ordered]@{ used_percentage = $pct; resets_at = $reset }
}

function ConvertTo-Reset($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [DateTime]) { return [DateTimeOffset]$v }
    try { return [DateTimeOffset]::Parse([string]$v, [Globalization.CultureInfo]::InvariantCulture) } catch { return $null }
}

# Several Claude Code sessions write the same file, each with its own last-received values.
# Never let an idle session's older numbers overwrite newer ones.
function Merge-UsageEntry($new, $old) {
    $now = [DateTimeOffset]::Now
    $newReset = ConvertTo-Reset $new.resets_at
    $oldReset = if ($old) { ConvertTo-Reset $old.resets_at } else { $null }
    $oldValid = $null -ne $old -and $null -ne $old.used_percentage -and $null -ne $oldReset -and $oldReset -gt $now
    if (-not $oldValid) { return $new }
    if ($null -eq $new.used_percentage -or $null -eq $newReset) {
        return [ordered]@{ used_percentage = [double]$old.used_percentage; resets_at = $oldReset.ToString('o') }
    }
    $diff = ($newReset - $oldReset).TotalSeconds
    if ($diff -gt 60) { return $new }   # new window started
    if ($diff -lt -60) { return [ordered]@{ used_percentage = [double]$old.used_percentage; resets_at = $oldReset.ToString('o') } }
    # Same window: usage only grows, so the higher value is the fresher one.
    if ([double]$old.used_percentage -gt $new.used_percentage) { $new.used_percentage = [double]$old.used_percentage }
    return $new
}

try {
    $dir = if ($env:USAGE_DIR) { $env:USAGE_DIR } else { 'C:\temp' }   # USAGE_DIR is for testing only
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $path = Join-Path $dir 'usage.json'
    $old = $null
    try {
        # Share delete/write so another session can replace the file while we read it.
        $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try { $old = (New-Object IO.StreamReader($fs)).ReadToEnd() | ConvertFrom-Json } finally { $fs.Dispose() }
    } catch { }
    $usage = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString('o')
        five_hour = Merge-UsageEntry (Get-UsageEntry $(if ($rl) { $rl.five_hour })) $(if ($old) { $old.five_hour })
        seven_day = Merge-UsageEntry (Get-UsageEntry $(if ($rl) { $rl.seven_day })) $(if ($old) { $old.seven_day })
    }
    $tmp = "$path.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($usage | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath $tmp -Destination $path -Force
} catch {
    if ($tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Output "$five | $week"
```

**Viktigt:** spara skriptet som **UTF-8 med BOM**. Windows PowerShell 5.1 läser annars filen med fel teckenkodning, och "å"/"ä" blir skräptecken. Om ditt skrivverktyg sparar utan BOM, spara om filen efteråt:

```powershell
$p = "$env:USERPROFILE\.claude\statusline.ps1"
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($p, $t, (New-Object Text.UTF8Encoding $true))
```

## 2. Uppdatera inställningarna

I `%USERPROFILE%\.claude\settings.json`: lägg till eller ersätt bara nyckeln `statusLine` med följande, och **behåll alla andra inställningar som de är**. Använd den faktiska användarmappen och snedstreck framåt i sökvägen.

```json
"statusLine": {
  "type": "command",
  "command": "powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/<ANVÄNDARNAMN>/.claude/statusline.ps1",
  "refreshInterval": 5
}
```

## 3. Testa

Testa mot en separat testmapp, **inte** mot `C:\temp`. Skriptet behåller det högsta värdet inom samma tidsfönster, så påhittade värden i den riktiga filen kan bli kvar tills fönstret återställs.

```powershell
$p = "$env:USERPROFILE\.claude\statusline.ps1"
$env:USAGE_DIR = Join-Path $env:TEMP 'usage-test'
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$j = '{"rate_limits":{"five_hour":{"used_percentage":42,"resets_at":' + ($now+8100) + '},"seven_day":{"used_percentage":10,"resets_at":' + ($now+273600) + '}}}'
$j | powershell -NoProfile -ExecutionPolicy Bypass -File $p
($j -replace '"used_percentage":42','"used_percentage":30') | powershell -NoProfile -ExecutionPolicy Bypass -File $p
'{}' | powershell -NoProfile -ExecutionPolicy Bypass -File $p
Get-Content "$env:USAGE_DIR\usage.json" -Raw
Remove-Item $env:USAGE_DIR -Recurse -Force; Remove-Item Env:USAGE_DIR
```

Förväntat resultat:
- Första raden: `5h: 42% (återställs om 2h 14m) | 7d: 10% (återställs om 3d 3h)` med korrekta "å" och "ä".
- Andra raden: `5h: 30% ...`. Statusraden visar sessionens egna värden.
- Tredje raden: `5h: inväntar data | 7d: inväntar data`.
- Filen innehåller ändå `42` och `10`. Det lägre värdet och den tomma körningen skrev inte över de högre värdena.

Visa mig utdata och filens innehåll.

## Bra att veta

- `rate_limits` finns bara för Pro- och Max-prenumeranter, och först efter sessionens första svar från API:et.
- Filen skrivs om var 5:e sekund, men siffrorna uppdateras bara när Claude Code (huvudsessionen eller en underagent) får ett svar från API:et. Användning på claude.ai syns först vid nästa anrop från Claude Code.
- Alla Claude Code-sessioner på datorn skriver till samma fil. Skriptet behåller alltid de färskaste värdena, så sessioner som står still skriver inte över nyare siffror.
- Filen innehåller bara tidsstämpel, procent och återställningstid. Inga andra sessionsuppgifter sparas.
````
