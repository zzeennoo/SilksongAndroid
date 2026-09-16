param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Depot,

    [Parameter(Mandatory = $false)]
    [string]$Output,

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 64)]
    [int]$Jobs = 0,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Vulkan", "OpenGLES3")]
    [string]$GraphicsApi = "Vulkan",

    [Parameter(Mandatory = $false)]
    [switch]$ValidatePathsOnly,

    # Write pc-output\texture-report.json for the depot and stop. Builds
    # nothing and writes nothing into the depot; see tools/pc-builder/README.md.
    [Parameter(Mandatory = $false)]
    [switch]$TextureReport,

    # With -TextureReport: skip reading texture payloads (faster, but cannot
    # tell which DXT1 textures use one-bit transparency).
    [Parameter(Mandatory = $false)]
    [switch]$SkipPayloadScan
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5 evaluates parameter default expressions before
# $PSScriptRoot is populated in some -File/drag-and-drop invocations. Resolve
# the script directory in the body, where both values are reliably available.
$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$RepoPath = [System.IO.Path]::GetFullPath($ScriptRoot)
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $RepoPath "pc-output"
}
if ($ValidatePathsOnly) {
    Write-Output "RepoPath=$RepoPath"
    Write-Output "OutputPath=$([System.IO.Path]::GetFullPath($Output))"
    return
}

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

Write-Host "Silksong Android PC builder" -ForegroundColor Cyan
Write-Host "  Linux depot: $DepotPath"
Write-Host "  Output:      $OutputPath"
if ($TextureReport) {
    Write-Host "  Mode:        texture report only (read-only, no build)"
} else {
    Write-Host "  Graphics:    $GraphicsApi"
    if ($Jobs -gt 0) { Write-Host "  Jobs:        $Jobs" }
    else { Write-Host "  Jobs:        automatic (memory-safe)" }
}
Write-Host ""
if (-not $TextureReport) {
    Write-Host "The first run downloads the Docker image, Android NDK, and pinned Unity tools."
    Write-Host "Later runs reuse all of them, including completed native object files."
    Write-Host ""
}

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
if ($TextureReport) {
    if ($SkipPayloadScan) {
        $DockerArgs += @("--env", "PC_SKIP_PAYLOAD_SCAN=1")
    }
    $DockerArgs += @($Image, "texture-report")
    & docker @DockerArgs
    if ($LASTEXITCODE -ne 0) { Stop-Build "the texture report failed. The last error above is the useful one." }
    Write-Host ""
    Write-Host "Texture report complete: $(Join-Path $OutputPath 'texture-report.json')" -ForegroundColor Green
    Write-Host "The summary above is total depot capacity, not what one scene keeps loaded."
    return
}
if ($GraphicsApi -eq "OpenGLES3") {
    $DockerArgs += @("--env", "PC_GRAPHICS_API=gles3")
}
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
