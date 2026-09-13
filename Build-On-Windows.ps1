param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Depot,

    [Parameter(Mandatory = $false)]
    [string]$Output = (Join-Path $PSScriptRoot "pc-output"),

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 64)]
    [int]$Jobs = 0
)

$ErrorActionPreference = "Stop"

function Stop-Build([string]$Message) {
    Write-Host "Silksong PC build: $Message" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Stop-Build "Docker Desktop is not installed or docker.exe is not on PATH."
}

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-Build "Docker Desktop is installed but is not running. Start it and try again."
}

$DepotPath = [System.IO.Path]::GetFullPath($Depot)
if (-not (Test-Path -LiteralPath $DepotPath -PathType Container)) {
    Stop-Build "the depot folder does not exist: $DepotPath"
}

$OutputPath = [System.IO.Path]::GetFullPath($Output)
[System.IO.Directory]::CreateDirectory($OutputPath) | Out-Null
$RepoPath = [System.IO.Path]::GetFullPath($PSScriptRoot)

Write-Host "Silksong Android PC builder" -ForegroundColor Cyan
Write-Host "  Linux depot: $DepotPath"
Write-Host "  Output:      $OutputPath"
if ($Jobs -gt 0) { Write-Host "  Jobs:        $Jobs" }
else { Write-Host "  Jobs:        automatic (memory-safe)" }
Write-Host ""
Write-Host "The first run downloads the Docker image, Android NDK, and pinned Unity tools."
Write-Host "Later runs reuse all of them, including completed native object files."
Write-Host ""

$Image = "silksong-pc-builder:latest"
docker build --platform linux/amd64 --target pc --tag $Image --file `
    (Join-Path $RepoPath "tools/docker/apk.Dockerfile") `
    (Join-Path $RepoPath "tools/docker")
if ($LASTEXITCODE -ne 0) { Stop-Build "the Docker image did not build." }

$DockerArgs = @(
    "run", "--rm", "--platform", "linux/amd64",
    "--volume", "${RepoPath}:/workspace",
    "--volume", "${DepotPath}:/game:ro",
    "--volume", "${OutputPath}:/pc-output",
    "--volume", "silksong-unity-player:/opt/unity-player",
    "--volume", "silksong-pc-build:/pc-cache",
    "--volume", "silksong-gradle:/gradle",
    "--volume", "silksong-nuget:/root/.nuget/packages",
    "--volume", "silksong-apk-build:/root/.cache/silksong",
    "--volume", "silksong-signing:/root/.android",
    "--env", "GRADLE_DAEMON=0"
)
if ($Jobs -gt 0) {
    $DockerArgs += @("--env", "PC_BUILD_JOBS=$Jobs")
}
$DockerArgs += @($Image, "pc")

& docker @DockerArgs
if ($LASTEXITCODE -ne 0) { Stop-Build "the game build failed. The last error above is the useful one." }

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "Install the APK from $OutputPath, copy the PC-Build ZIP to the device,"
Write-Host "then choose Import PC build in the launcher. Do not extract the ZIP."
