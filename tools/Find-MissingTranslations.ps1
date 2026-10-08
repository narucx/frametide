# Dev tool: lists UI texts that have no entry in src/Frametide/Lang/<lang>.json.
#   powershell -ExecutionPolicy Bypass -File tools\Find-MissingTranslations.ps1 -Lang de [-RemoveUnused]
# Finds texts passed to T(...), RunAsync(...) (status bar), Text(...), Button(...), Badge(...), Row(...), the tweak/repair/profile catalog
# (Name, Description, BlockedHint), ("text", Enum.Value) option tables, FPS limit sources, ["key"] = "text" tables
# and static XAML texts (Text, Content, ToolTip).
# -RemoveUnused deletes entries whose text no longer appears anywhere in the source.
param([string]$Lang = 'de', [switch]$RemoveUnused)

$root = Split-Path $PSScriptRoot -Parent
$langFile = Join-Path $root "src\Frametide\Lang\$Lang.json"
Add-Type -AssemblyName System.Web.Extensions
$map = (New-Object System.Web.Script.Serialization.JavaScriptSerializer).DeserializeObject([IO.File]::ReadAllText($langFile, [Text.Encoding]::UTF8))

$str = '"((?:[^"\\]|\\.)*)"'
$csPatterns = @(
    "\bT\($str", "\bRunAsync\($str", "\bText\($str", "\bButton\($str", "\bBadge\($str", "\bRow\(""\w+"", $str", "\bRow\(null, $str",
    "\bName = $str", "\bDescription = $str", "\bBlockedHint = $str", "new\(""[^""]+"",\s*$str", "^\s*new\($str",
    "(?<=[\[,]\s*)\($str,\s*[A-Z]\w+\.[A-Z]\w+\)", "new FpsLimit\($str", "\[""[^""]+""\] = $str"
)
$found = New-Object System.Collections.Generic.HashSet[string]
foreach ($f in Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $text = [IO.File]::ReadAllText($f.FullName)
    foreach ($p in $csPatterns) {
        foreach ($m in [regex]::Matches($text, $p, 'Multiline')) {
            $s = $m.Groups[1].Value -replace '\\"', '"' -replace '\\n', "`n" -replace '\\\\', '\'
            if ($s -match '[A-Za-z]{2}' -and $s -cnotmatch '^(HK|reg\||svc\||pnp\|)' -and $s -cnotmatch '^[a-z0-9_.]+$' -and $s -notmatch '^[^ ]+\.exe$') { [void]$found.Add($s) }
        }
    }
}
# Every (non-interpolated) string inside new SystemCheck(...): name, value and text are all translated.
foreach ($f in Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    foreach ($call in [regex]::Matches([IO.File]::ReadAllText($f.FullName), 'new SystemCheck\([^;]*?\)\)?[,;]')) {
        # Drop interpolated strings ($"...") first, they are values, not translatable texts.
        $plain = [regex]::Replace($call.Value, '\$"(?:[^"\\]|\\.)*"', '')
        foreach ($m in [regex]::Matches($plain, $str)) {
            $s = $m.Groups[1].Value -replace '\\"', '"'
            if ($s -match '[A-Za-z]{2}') { [void]$found.Add($s) }
        }
    }
}
foreach ($f in Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $text = [IO.File]::ReadAllText($f.FullName)
    foreach ($m in [regex]::Matches($text, '\b(?:Text|Content|ToolTip)="([^"{]+)"')) {
        $s = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
        if ($s -match '[A-Za-z]{2}' -and $s -notin 'FRAME', 'TIDE', 'START') { [void]$found.Add($s) }
    }
}
$missing = @($found | Where-Object { -not $map.ContainsKey($_) } | Sort-Object)
"{0} texts found, {1} missing in {2}.json" -f $found.Count, $missing.Count, $Lang
$missing

# Texts also used as format strings or messages passed through variables (not found by the patterns above).
$keepExtra = @('Running: {0} ...', 'System Restore is turned off. Turn it on: System Properties > System Protection > drive C: > Configure.')
if ($RemoveUnused) {
    $js = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $lines = foreach ($k in $map.Keys) {
        if ($found.Contains($k) -or $keepExtra -contains $k) { '  ' + $js.Serialize($k) + ': ' + $js.Serialize($map[$k]) }
    }
    $json = "{`n" + ($lines -join ",`n") + "`n}`n"
    # The serializer escapes non-ASCII as \uXXXX; keep the file readable (quotes and backslashes stay escaped).
    $json = [regex]::Replace($json, '\\u([0-9a-fA-F]{4})', { param($m) $ch = [char][Convert]::ToInt32($m.Groups[1].Value, 16); if ($ch -eq '"' -or $ch -eq '\') { $m.Value } else { [string]$ch } })
    [IO.File]::WriteAllText($langFile, $json, (New-Object Text.UTF8Encoding $false))
    "Removed {0} unused entries, {1} left." -f ($map.Count - @($lines).Count), @($lines).Count
}
