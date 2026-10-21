# Wicked terminal integration for PowerShell: marks round each prompt, command and result, and the working directory.
if ($Global:__WickedLoaded) { return }
$Global:__WickedLoaded = $true

$Global:__WickedOriginalPrompt = $function:prompt
$Global:__WickedHistoryId = -1

function Global:__WickedEscape([string]$text) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($ch in $text.ToCharArray()) {
        if ($ch -eq '\') { [void]$builder.Append('\\') }
        elseif ($ch -eq ';') { [void]$builder.Append('\x3b') }
        elseif ([int]$ch -lt 32) { [void]$builder.Append(('\x{0:x2}' -f [int]$ch)) }
        else { [void]$builder.Append($ch) }
    }
    $builder.ToString()
}

function Global:prompt {
    $success = $?
    $esc = [char]27
    $bell = [char]7
    $out = ''
    $last = Get-History -Count 1
    if ($last -and $Global:__WickedHistoryId -ne $last.Id) {
        $code = if ($success) { 0 } else { 1 }
        $out += "$esc]133;D;$code$bell"
    }
    $Global:__WickedHistoryId = if ($last) { $last.Id } else { 0 }
    $path = (Get-Location).ProviderPath -replace '\\', '/'
    $out += "$esc]7;file://$([System.Net.Dns]::GetHostName())/$($path.TrimStart('/'))$bell"
    $out += "$esc]133;A$bell"
    $prompt = & $Global:__WickedOriginalPrompt
    "$out$prompt$esc]133;B$bell"
}

if (Get-Module -ListAvailable PSReadLine) {
    Set-PSReadLineKeyHandler -Chord Enter -ScriptBlock {
        $line = $null
        $cursor = $null
        [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
        $esc = [char]27
        $bell = [char]7
        [Console]::Write("$esc]633;E;$(__WickedEscape $line)$bell$esc]133;C$bell")
        [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
    }
}
