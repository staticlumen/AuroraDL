$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectCandidates = @(
  (Join-Path $root "AuroraDL\auroradl.csproj"),
  (Join-Path $root "auroradl\auroradl.csproj"),
  (Join-Path $root "NativeSpectrometer\auroradl.csproj")
)
$project = $projectCandidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1

if (-not $project) {
  Write-Host "Project not found. Checked:"
  $projectCandidates | ForEach-Object { Write-Host " - $_" }
  exit 1
}

$dotnetInfo = & dotnet --list-sdks 2>$null
if (-not $dotnetInfo) {
  Write-Host "No .NET SDK found."
  Write-Host "Install .NET 8 SDK (x64): https://dotnet.microsoft.com/download/dotnet/8.0"
  exit 1
}

Write-Host "Starting auroradl..."
& dotnet run --project $project -c Release