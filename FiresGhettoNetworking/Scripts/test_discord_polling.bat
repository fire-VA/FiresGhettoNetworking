@echo off
REM Test script to verify Discord reaction polling works
REM Run this while your server is stopped to test if restart detection works

echo ========================================
echo Discord Restart Reaction Test
echo ========================================
echo.

SET "SERVER_DIR=%~dp0..\..\"
cd /d "%SERVER_DIR%"

SET "CONFIG_FILE=%SERVER_DIR%BepInEx\config\com.Fire.FiresGhettoNetworkMod.cfg"
SET "SENTINEL=%SERVER_DIR%BepInEx\config\VAGhettoNetworking\server_heartbeat_ghetto.json"
SET "RESTART_FLAG=%SERVER_DIR%BepInEx\plugins\restart_requested.flag"

echo Server Directory: %SERVER_DIR%
echo Config File: %CONFIG_FILE%
echo Sentinel File: %SENTINEL%
echo.

REM Check if files exist
IF NOT EXIST "%CONFIG_FILE%" (
    echo ERROR: Config file not found!
    echo Expected: %CONFIG_FILE%
    pause
    exit /b 1
)

IF NOT EXIST "%SENTINEL%" (
    echo ERROR: Sentinel file not found!
    echo Expected: %SENTINEL%
    echo This file is created when the server starts.
    echo Make sure you've started the server at least once.
    pause
    exit /b 1
)

echo Files found, parsing config...
echo.

REM Run the PowerShell polling logic with verbose output
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { $configPath = '%CONFIG_FILE%'; $sentinelPath = '%SENTINEL%'; $restartFlagPath = '%RESTART_FLAG%'; Write-Host 'Reading config file...'; $config = Get-Content $configPath -Raw; $botToken = ($config -split \"`n\" | Where-Object { $_ -match '^Discord Bot Token\\s*=\\s*(.+)' } | Select-Object -First 1) -replace '^Discord Bot Token\\s*=\\s*',''; $channelId = ($config -split \"`n\" | Where-Object { $_ -match '^Status Channel ID\\s*=\\s*(.+)' } | Select-Object -First 1) -replace '^Status Channel ID\\s*=\\s*',''; if ([string]::IsNullOrWhiteSpace($botToken)) { Write-Host 'ERROR: Bot token not found in config' -ForegroundColor Red; exit 1; }; if ([string]::IsNullOrWhiteSpace($channelId)) { Write-Host 'ERROR: Channel ID not found in config' -ForegroundColor Red; exit 1; }; $botToken = $botToken.Trim(); $channelId = $channelId.Trim(); Write-Host \"Bot Token: $($botToken.Substring(0, [Math]::Min(20, $botToken.Length)))...\" -ForegroundColor Green; Write-Host \"Channel ID: $channelId\" -ForegroundColor Green; Write-Host ''; Write-Host 'Reading sentinel file...'; $sentinelContent = Get-Content $sentinelPath -Raw; Write-Host \"Sentinel content: $sentinelContent\"; $sentinelJson = $sentinelContent | ConvertFrom-Json; $messageId = $sentinelJson.messageId; if ([string]::IsNullOrWhiteSpace($messageId)) { Write-Host 'ERROR: Message ID not found in sentinel file' -ForegroundColor Red; Write-Host 'The server needs to post a status message first.' -ForegroundColor Yellow; exit 1; }; Write-Host \"Message ID: $messageId\" -ForegroundColor Green; Write-Host ''; Write-Host 'Checking Discord for restart reactions...' -ForegroundColor Cyan; $emoji = [System.Uri]::EscapeDataString([string][char]0x1F504); $url = \"https://discord.com/api/v10/channels/$channelId/messages/$messageId/reactions/$emoji`?limit=10\"; Write-Host \"API URL: $url\"; $headers = @{ 'Authorization' = \"Bot $botToken\" }; try { $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 10 -ErrorAction Stop; Write-Host ''; Write-Host 'API call successful!' -ForegroundColor Green; Write-Host \"Found $($response.Count) reaction(s)\"; if ($response -and $response.Count -gt 0) { Write-Host ''; Write-Host 'Users who reacted:' -ForegroundColor Cyan; foreach ($user in $response) { if ($user.bot) { Write-Host \"  - $($user.username) (BOT - will be ignored)\" -ForegroundColor Gray; } else { Write-Host \"  - $($user.username) (ID: $($user.id))\" -ForegroundColor Yellow; $content = \"Restart requested by $($user.username) at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')) UTC\"; Set-Content -Path $restartFlagPath -Value $content -Force; Write-Host ''; Write-Host 'SUCCESS: Restart flag created!' -ForegroundColor Green; Write-Host \"Flag path: $restartFlagPath\"; Write-Host ''; Write-Host 'If this were the real server wrapper, it would restart now.' -ForegroundColor Cyan; exit 0; } } Write-Host ''; Write-Host 'No non-bot reactions found.' -ForegroundColor Yellow; Write-Host 'Click the restart emoji (??) in Discord and run this test again.' -ForegroundColor Yellow; } else { Write-Host ''; Write-Host 'No reactions found on this message.' -ForegroundColor Yellow; Write-Host 'Click the restart emoji (??) in Discord and run this test again.' -ForegroundColor Yellow; } } catch { Write-Host ''; Write-Host 'ERROR calling Discord API:' -ForegroundColor Red; Write-Host $_.Exception.Message -ForegroundColor Red; if ($_.Exception.Response) { Write-Host \"HTTP Status: $($_.Exception.Response.StatusCode)\" -ForegroundColor Red; }; exit 1; } }"

echo.
echo ========================================
echo Test Complete
echo ========================================
pause
