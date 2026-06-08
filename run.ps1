Param(
    [string]$Configuration = "Debug",
    [string]$Framework = "net10.0-windows10.0.26100.0",
    [string]$LaunchProfile = ""
)

$cmd = "dotnet run -c $Configuration -f $Framework"
if ($LaunchProfile -ne "") {
    $cmd += " --launch-profile `"$LaunchProfile`""
}

Write-Host "Running: $cmd"
Invoke-Expression $cmd
