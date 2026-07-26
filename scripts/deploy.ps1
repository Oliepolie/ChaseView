<#
.SYNOPSIS
  Build a Nuclear Option BepInEx plugin, deploy it safely, and VERIFY it actually loaded.

.DESCRIPTION
  Every step here exists because its absence produced a silent failure on a real project:

    1. Refuse to run while the game is alive.
         Copying a DLL over one the game has mapped can raise a scan-time sharing violation
         inside BepInEx's plugin scan. BepInEx logs it and continues; the mod SILENTLY does
         not load. You then spend the session debugging the PREVIOUS build's behaviour.

    2. Stage-then-rename (write to <name>.dll.staging, then Move-Item over the target).
         A rename on the same volume is close to atomic, so the plugins folder never contains
         a half-written DLL.

    3. Assert exactly ONE copy of the DLL under BepInEx/plugins.
         A stray copy left at plugins/YourMod.dll while the good one lives at
         plugins/YourMod/YourMod.dll makes BepInEx pick one and log
         "Skipping [<Name> <ver>] because a newer version exists (<ver>)".
         The copy it skips can be the CORRECT one — the one sitting beside its AssetBundle.
         The mod then loads, runs, finds no bundle next to Assembly.Location, and quietly
         takes a fallback code path. Measured on GroundZero (one mod's incident): a whole map
         baked down a fallback path while every config value still reported the fast path on.

    4. Read only the FRESH log, and prove it is fresh from the plugin's OWN version line.
         File timestamps and the BepInEx log header both lied on this project; a leftover
         "-batchmode" process holding the log meant a stale file was tailed and an entire
         milestone was falsely marked PASSED. Your own emitted version line is the only
         freshness signal you control. Emit one in Awake:
             Log.LogInfo($"{PluginName} v{PluginVersion} loaded");

  Parameterised on game path, mod folder and DLL name so it is reusable across mods.

.EXAMPLE
  .\deploy.ps1 -ModFolder GroundZero -DllName GroundDeformation.dll

.EXAMPLE
  # Deploy an already-built DLL without rebuilding, and copy assets beside it.
  .\deploy.ps1 -ModFolder MyMod -DllName MyMod.dll -SkipBuild -ExtraFiles myassets.bundle

.EXAMPLE
  # Deploy, then launch nothing; just verify a log you captured earlier.
  .\deploy.ps1 -ModFolder MyMod -DllName MyMod.dll -VerifyOnly
#>

[CmdletBinding()]
param(
    # Root of the Nuclear Option install (the folder containing NuclearOption.exe).
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',

    # Folder name under BepInEx/plugins/. The DLL and any colocated assets go here.
    # Colocation matters: read-only assets are resolved from Assembly.Location at runtime.
    [Parameter(Mandatory = $true)]
    [string] $ModFolder,

    # Output DLL file name, e.g. MyMod.dll. Must match <AssemblyName> in the csproj.
    [Parameter(Mandatory = $true)]
    [string] $DllName,

    # Project directory (defaults to the repo root two levels up from this script's
    # typical scripts/ location, else the current directory).
    [string] $ProjectDir,

    # Extra files to copy beside the DLL (AssetBundles, shipped read-only data).
    # These MUST land in the same folder as the DLL or Assembly.Location-relative
    # loads return null and the mod falls back silently.
    [string[]] $ExtraFiles = @(),

    [string] $Configuration = 'Release',

    # Substring that must appear in the fresh log to prove your build loaded.
    # Defaults to the DLL base name; override if your version line uses a display name
    # that differs from the assembly name (they are independent identities).
    [string] $VersionLineMatch,

    [switch] $SkipBuild,

    # Skip build+copy; only run the post-launch log verification.
    [switch] $VerifyOnly,

    # Process name to check for (no .exe). Override if the executable is renamed.
    [string] $GameProcessName = 'NuclearOption'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

function Write-Step  { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok    { param([string]$m) Write-Host "    PASS  $m" -ForegroundColor Green }
function Write-Warn2 { param([string]$m) Write-Host "    WARN  $m" -ForegroundColor Yellow }
function Write-Fail  { param([string]$m) Write-Host "    FAIL  $m" -ForegroundColor Red }

if (-not $VersionLineMatch) {
    $VersionLineMatch = [System.IO.Path]::GetFileNameWithoutExtension($DllName)
}

if (-not $ProjectDir) {
    if ($PSScriptRoot) { $ProjectDir = Split-Path -Parent $PSScriptRoot }
    if (-not $ProjectDir -or -not (Test-Path $ProjectDir)) { $ProjectDir = (Get-Location).Path }
}

$PluginsRoot = Join-Path $GamePath 'BepInEx\plugins'
$TargetDir   = Join-Path $PluginsRoot $ModFolder
$TargetDll   = Join-Path $TargetDir $DllName
$LogPath     = Join-Path $GamePath 'BepInEx\LogOutput.log'

Write-Host ''
Write-Host "Nuclear Option / BepInEx deploy" -ForegroundColor White
Write-Host "  game     : $GamePath"
Write-Host "  project  : $ProjectDir"
Write-Host "  target   : $TargetDll"
Write-Host ''

# ---------------------------------------------------------------------------
# 0. Sanity: the install actually looks like a BepInEx'd Nuclear Option
# ---------------------------------------------------------------------------

if (-not (Test-Path $GamePath)) {
    Write-Fail "Game path does not exist: $GamePath"
    exit 1
}
$managed = Join-Path $GamePath 'NuclearOption_Data\Managed\Assembly-CSharp.dll'
if (-not (Test-Path $managed)) {
    Write-Warn2 "No Assembly-CSharp.dll at $managed — is -GamePath right? (Continuing.)"
}
if (-not (Test-Path (Join-Path $GamePath 'BepInEx'))) {
    Write-Fail "No BepInEx folder under $GamePath. Install BepInEx (x64, Mono — this game is Mono, not Il2Cpp) and run the game once so it generates its folders."
    exit 1
}

# ---------------------------------------------------------------------------
# 1. Refuse to run while the game is alive
#
#    Also catches leftover headless instances: a "-batchmode" process from a
#    previous server test holds the log file open, so you tail a stale log and
#    read old results as if they were new.
# ---------------------------------------------------------------------------

if (-not $VerifyOnly) {
    Write-Step "Checking the game is closed"
    $procs = @(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        Write-Fail "'$GameProcessName' is running (PID(s): $($procs.Id -join ', '))."
        Write-Host  "          Close it before deploying. Overwriting a mapped DLL can raise a"
        Write-Host  "          sharing violation during BepInEx's plugin scan, after which the mod"
        Write-Host  "          SILENTLY does not load and you debug the previous build."
        exit 1
    }
    Write-Ok "no '$GameProcessName' process"

    # A headless/dedicated instance may run under the same or a different name; look
    # for any process whose command line mentions the game folder.
    try {
        $stray = @(Get-CimInstance Win32_Process -ErrorAction Stop |
                   Where-Object { $_.CommandLine -and $_.CommandLine -like "*$GamePath*" })
        if ($stray.Count -gt 0) {
            Write-Warn2 "Process(es) referencing the game folder are still alive:"
            foreach ($s in $stray) { Write-Host "            PID $($s.ProcessId)  $($s.Name)" }
            Write-Warn2 "A leftover -batchmode instance holds the log open; kill it or you will read a stale log."
        }
    } catch {
        Write-Warn2 "Could not enumerate command lines ($($_.Exception.Message)) — skipping stray-process check."
    }
}

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------

$builtDll = $null

if (-not $VerifyOnly -and -not $SkipBuild) {
    Write-Step "dotnet build -c $Configuration"
    Push-Location $ProjectDir
    try {
        & dotnet build -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { Write-Fail "build failed (exit $LASTEXITCODE)"; exit $LASTEXITCODE }
    } finally {
        Pop-Location
    }
    Write-Ok "build succeeded"
}

if (-not $VerifyOnly) {
    $candidates = @(Get-ChildItem -Path (Join-Path $ProjectDir 'bin') -Recurse -Filter $DllName -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -like "*\$Configuration\*" } |
                    Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -eq 0) {
        Write-Fail "Could not find a built '$DllName' under $ProjectDir\bin\...\$Configuration\."
        Write-Host  "          Check <AssemblyName> in the csproj matches -DllName."
        exit 1
    }
    $builtDll = $candidates[0].FullName
    Write-Ok "built artifact: $builtDll  ($([int]$candidates[0].Length) bytes, $($candidates[0].LastWriteTime))"
}

# ---------------------------------------------------------------------------
# 3. Stage-copy, then rename into place
# ---------------------------------------------------------------------------

if (-not $VerifyOnly) {
    Write-Step "Deploying to $TargetDir"
    if (-not (Test-Path $TargetDir)) {
        New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
        Write-Ok "created $TargetDir"
    }

    $staging = "$TargetDll.staging"
    Copy-Item -LiteralPath $builtDll -Destination $staging -Force
    Move-Item -LiteralPath $staging  -Destination $TargetDll -Force
    Write-Ok "$DllName in place"

    foreach ($f in $ExtraFiles) {
        $src = $f
        if (-not [System.IO.Path]::IsPathRooted($src)) {
            $src = Join-Path $ProjectDir $f
            if (-not (Test-Path $src)) {
                # also try beside the built DLL (bundles are often copied by the build)
                $alt = Join-Path (Split-Path -Parent $builtDll) (Split-Path -Leaf $f)
                if (Test-Path $alt) { $src = $alt }
            }
        }
        if (-not (Test-Path $src)) {
            Write-Fail "ExtraFile not found: $f"
            Write-Host  "          Colocated assets are resolved from Assembly.Location at runtime."
            Write-Host  "          A missing bundle does NOT stop the mod loading — it silently takes"
            Write-Host  "          a fallback path, which is a far more expensive failure than this one."
            exit 1
        }
        $dstName = Split-Path -Leaf $src
        $dst     = Join-Path $TargetDir $dstName
        Copy-Item -LiteralPath $src -Destination "$dst.staging" -Force
        Move-Item -LiteralPath "$dst.staging" -Destination $dst -Force
        Write-Ok "colocated $dstName"
    }
}

# ---------------------------------------------------------------------------
# 4. Assert exactly ONE copy of the DLL under BepInEx/plugins
#
#    BepInEx loads every DLL it finds and, on a GUID collision, logs
#      Skipping [<Name> <version>] because a newer version exists (<version>)
#    and drops one. The dropped one can be the copy that sits beside its assets.
# ---------------------------------------------------------------------------

Write-Step "Checking for duplicate copies of $DllName under plugins/"
$copies = @(Get-ChildItem -Path $PluginsRoot -Recurse -Filter $DllName -ErrorAction SilentlyContinue)
if ($copies.Count -eq 0) {
    Write-Fail "No copy of $DllName found under $PluginsRoot"
    exit 1
} elseif ($copies.Count -gt 1) {
    Write-Fail "$($copies.Count) copies of $DllName found:"
    foreach ($c in $copies) { Write-Host "            $($c.FullName)   ($($c.LastWriteTime))" }
    Write-Host  "          BepInEx will load one and skip the other. The skipped one may be the"
    Write-Host  "          copy beside its AssetBundle — the mod then runs a silent fallback path."
    Write-Host  "          Delete the stray copies and re-run."
    exit 1
}
Write-Ok "exactly one copy: $($copies[0].FullName)"

# Leftover staging files mean a previous run died between Copy and Move.
$stale = @(Get-ChildItem -Path $PluginsRoot -Recurse -Filter '*.staging' -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    Write-Warn2 "Leftover .staging files (a previous deploy was interrupted):"
    foreach ($s in $stale) { Write-Host "            $($s.FullName)" }
}

# ---------------------------------------------------------------------------
# 5. Verify the log — only meaningful AFTER you launch the game
#
#    Freshness comes from YOUR version line, not from the file timestamp and not
#    from BepInEx's own header. Both were observed to lie on this project.
# ---------------------------------------------------------------------------

Write-Step "Verifying the BepInEx log"

if (-not (Test-Path $LogPath)) {
    Write-Warn2 "No log at $LogPath yet."
    Write-Host  "          Launch the game, then re-run with -VerifyOnly to check the load."
    Write-Host ''
    Write-Host  "DEPLOY OK — log verification pending." -ForegroundColor Green
    exit 0
}

$logInfo = Get-Item $LogPath
$dllInfo = Get-Item $copies[0].FullName
$logIsOlderThanDll = $logInfo.LastWriteTime -lt $dllInfo.LastWriteTime

if ($logIsOlderThanDll) {
    Write-Warn2 "Log is older than the deployed DLL — it predates this build."
    Write-Host  "          Launch the game, then re-run with -VerifyOnly."
    Write-Host ''
    Write-Host  "DEPLOY OK — log verification pending." -ForegroundColor Green
    exit 0
}

$log = Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue

# 5a. Your own version line — the sole freshness signal.
$versionLines = @($log | Select-String -SimpleMatch $VersionLineMatch | Select-Object -Last 5)
if ($versionLines.Count -eq 0) {
    Write-Fail "No line matching '$VersionLineMatch' in the log — the plugin did not load, or it loaded and logged nothing."
    Write-Host  "          Emit one unconditional line in Awake and treat it as the ONLY freshness"
    Write-Host  "          signal (file timestamps and the BepInEx log header both lie):"
    Write-Host  "              Log.LogInfo(`$`"{PluginName} v{PluginVersion} loaded`");"
    $verdictLoad = $false
} else {
    Write-Ok "plugin line(s) present:"
    foreach ($v in $versionLines) { Write-Host "            $($v.Line.Trim())" }
    $verdictLoad = $true
}

# 5b. Shadowed duplicate — BepInEx dropping one of two plugins with the same GUID.
$skips = @($log | Select-String -SimpleMatch 'Skipping [')
if ($skips.Count -gt 0) {
    Write-Fail "BepInEx skipped a plugin:"
    foreach ($s in $skips) { Write-Host "            $($s.Line.Trim())" }
    Write-Host  "          Two copies with the same GUID were present at scan time. The one it kept"
    Write-Host  "          is not necessarily the one beside your assets."
    $verdictSkip = $false
} else {
    Write-Ok "no 'Skipping [' lines"
    $verdictSkip = $true
}

# 5c. Anything that looks like a failed load or a throwing patch.
#     A Harmony patch that throws is logged and then silently no-ops — the single
#     likeliest cause of "my code never ran".
$errPatterns = @('Sharing violation', 'Could not load', 'FileLoadException',
                 'ReflectionTypeLoadException', 'HarmonyException', 'Patching exception',
                 'MissingMethodException', 'TypeLoadException')
$errs = @()
foreach ($p in $errPatterns) { $errs += @($log | Select-String -SimpleMatch $p) }
if ($errs.Count -gt 0) {
    Write-Warn2 "Load/patch problems in the log:"
    foreach ($e in ($errs | Select-Object -First 12)) { Write-Host "            $($e.Line.Trim())" }
    Write-Host  "          A Harmony patch that throws is logged once and then does nothing —"
    Write-Host  "          the game keeps running and your hook is a silent no-op."
} else {
    Write-Ok "no load/patch exceptions matched"
}

Write-Host ''
if ($verdictLoad -and $verdictSkip) {
    Write-Host "DEPLOY VERIFIED — the plugin loaded and nothing shadowed it." -ForegroundColor Green
    Write-Host "Next: confirm the RESOLVED config in the log. BepInEx's Bind() never overwrites a"
    Write-Host "key already on disk, so a changed shipped default silently never reaches an existing"
    Write-Host "install — including yours. Dump the effective values unconditionally at Awake."
    exit 0
} else {
    Write-Host "DEPLOY NOT VERIFIED — see FAIL lines above." -ForegroundColor Red
    exit 1
}