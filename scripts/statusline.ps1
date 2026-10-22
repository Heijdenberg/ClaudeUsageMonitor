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
