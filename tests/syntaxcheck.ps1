param([string]$File)
$e = $null; $t = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($File, [ref]$t, [ref]$e)
if ($e) { $e | Select-Object -First 3 | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" } } else { 'syntax ok' }
