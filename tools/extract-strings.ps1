# Extracts every localizable English key from the sources and lists the ones missing a Spanish translation.
param([string]$Root = ".", [string]$Out = "strings-missing.txt")
$utf8 = New-Object System.Text.UTF8Encoding $false
$keys = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::Ordinal)

Get-ChildItem (Join-Path $Root 'src') -Recurse -Include *.cs, *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -ne 'Strings.es.cs' } | ForEach-Object {
    $t = [IO.File]::ReadAllText($_.FullName, $utf8)
    if ($_.Extension -eq '.xaml') {
        foreach ($m in [regex]::Matches($t, "\{l:T '((?:[^']|'')*)'\}")) { [void]$keys.Add($m.Groups[1].Value.Replace("''", "'")) }
    } else {
        foreach ($m in [regex]::Matches($t, 'Loc\.[TF]\(\s*"((?:[^"\\]|\\.)*)"')) { [void]$keys.Add([regex]::Unescape($m.Groups[1].Value)) }
        # Strings shown through the Tr converter or Loc.T(variable): page titles, tools, statuses, purposes, etc.
        foreach ($m in [regex]::Matches($t, 'new\(PageKey\.\w+, "([^"]+)", "([^"]+)"')) { [void]$keys.Add($m.Groups[1].Value); [void]$keys.Add($m.Groups[2].Value) }
        foreach ($m in [regex]::Matches($t, '(?:Name|Description|Title|Symptoms|Category) = "((?:[^"\\]|\\.)+)"')) { [void]$keys.Add([regex]::Unescape($m.Groups[1].Value)) }
        foreach ($m in [regex]::Matches($t, '\("(\w+)", "([A-Z][^"]+)"\)')) { [void]$keys.Add($m.Groups[2].Value) }
        # Diag("id", "Category", 0xGLYPH, "Title", "Description", "Symptoms", ...)
        foreach ($m in [regex]::Matches($t, 'Diag\("[^"]+",\s*"((?:[^"\\]|\\.)+)",\s*0x[0-9A-Fa-f]+,\s*"((?:[^"\\]|\\.)+)",\s*"((?:[^"\\]|\\.)+)",\s*"((?:[^"\\]|\\.)+)"')) { 1..4 | ForEach-Object { [void]$keys.Add([regex]::Unescape($m.Groups[$_].Value)) } }
        foreach ($m in [regex]::Matches($t, 'new\("([^"]+)", "([^"]+)"\)')) { [void]$keys.Add($m.Groups[1].Value); [void]$keys.Add($m.Groups[2].Value) }
    }
}

# Values that reach the UI through the Tr converter
$extra = @('Healthy','Warning','Unhealthy','Unknown','Running','Stopped','Paused','StartPending','StopPending','Automatic','Manual','Disabled','AutomaticDelayedStart',
  'Up','Down','Dormant','NotPresent','LowerLayerDown','Testing','Established','Listen','Listening','TimeWait','CloseWait','SynSent','SynReceived','FinWait1','FinWait2','Closing','LastAck','Closed','DeleteTcb',
  'Static','Dynamic','Other','Critical','Error','Information','All','Info','Debug','Ping','DNS Lookup','Traceroute','Interfaces','Connections','ARP',
  'System default','Custom','Reply','Timed out','Host unreachable','Network unreachable','TTL expired','Destination unreachable','Hop','Destination',
  'Overview','Tools','Analysis','System','low','medium','high','xhigh','None','Start','Stop','Restart',
  'Windows Update','Component servicing','Pending file operations','sensor','Unspecified','ACPI thermal zone',
  'Reliability counters require administrator privileges','Reliability counters not available','SMART data requires administrator privileges','SMART data not available',
  'RunOnce entries run a single time and cannot be disabled','Requires administrator privileges','Current user','All users','All users (32-bit)','Current user (once)','All users (once)',
  'Excellent','Good','Needs attention','Poor','Dark','Light','Use Windows setting','Timeouts','Connection failures','Name resolution','Memory pressure','Permission problems',
  'Disk and I/O','Null references','TLS / certificates','Crashes','Database','Missing files','Windows servicing','critical','error','warning','info','debug','Discharging','On AC power','Fully charged','Low','Charging','Partially charged','Size','Install date','Publisher','Name','All','Repairs','Diagnostics')
foreach ($e in $extra) { [void]$keys.Add($e) }

$existing = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$es = Join-Path $Root 'src/PCCommandCenter.Core/Localization/Strings.es.cs'
if (Test-Path $es) {
    $t = [IO.File]::ReadAllText($es, $utf8)
    foreach ($m in [regex]::Matches($t, '\["((?:[^"\\]|\\.)*)"\]\s*=')) { [void]$existing.Add([regex]::Unescape($m.Groups[1].Value)) }
}
$missing = $keys | Where-Object { -not $existing.Contains($_) }
[IO.File]::WriteAllLines($Out, [string[]]$missing, $utf8)
"keys: $($keys.Count)  missing: $(@($missing).Count)  -> $Out"
