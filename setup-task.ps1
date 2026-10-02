#Requires -RunAsAdministrator
# Usage (elevated PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\setup-task.ps1 -ChildUser <his account name>

param(
    [Parameter(Mandatory = $true)]
    [string]$ChildUser,

    [string]$ExePath = "C:\Windows\System32\PcTimeGuard\PcTimeGuard.exe",

    [string]$TaskName = "PcTimeGuard"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Executable not found: $ExePath"
}

# Resolve to the canonical COMPUTER\user form, and fail early if the account doesn't exist.
$sid = (New-Object System.Security.Principal.NTAccount($ChildUser)).Translate([System.Security.Principal.SecurityIdentifier])
$account = $sid.Translate([System.Security.Principal.NTAccount]).Value

$escapedAccount = [System.Security.SecurityElement]::Escape($account)
$escapedExe = [System.Security.SecurityElement]::Escape($ExePath)

# No <Duration> in <Repetition> means "repeat indefinitely".
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Enforces the PC usage schedule.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <Enabled>true</Enabled>
      <UserId>$escapedAccount</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$escapedExe</Command>
    </Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $TaskName -Xml $xml -Force | Out-Null

Write-Host "Scheduled task '$TaskName' registered:"
Write-Host "  Runs as        : SYSTEM"
Write-Host "  Trigger        : logon of $account, repeated every 5 minutes indefinitely"
Write-Host "  Action         : $ExePath"
Write-Host "  If running     : do not start a new instance"
Write-Host "  Battery/network: no restrictions"
