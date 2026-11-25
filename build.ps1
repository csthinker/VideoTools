# VideoTools 构建脚本（含 MCP / CLI 自动化组件）
# 用法：powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Configuration Release] [-SkipGui] [-RoslynPath <目录>]
param(
    [string]$Configuration = "Release",
    [switch]$SkipGui,
    [string]$RoslynPath,
    [switch]$DownloadRoslyn,
    [switch]$Portable,
    [string]$FfmpegPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$roslynVersion = "4.13.0"

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
        if ($path -and (Test-Path $path)) { return @{ Path = $path; IsFull = $true } }
    }
    $frameworkMsBuild = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    if (Test-Path $frameworkMsBuild) { return @{ Path = $frameworkMsBuild; IsFull = $false } }
    throw "未找到 MSBuild。请安装 Visual Studio 2022（含 .NET 桌面开发工作负载）。"
}

function Ensure-Roslyn {
    param([string]$RequestedPath)
    if ($RequestedPath) {
        if (-not (Test-Path (Join-Path $RequestedPath "csc.exe"))) { throw "RoslynPath 中未找到 csc.exe：$RequestedPath" }
        return $RequestedPath
    }

    $cached = Join-Path $env:TEMP "roslyn_toolset\tasks\net472"
    if (Test-Path (Join-Path $cached "csc.exe")) { return $cached }

    $cacheRoot = Join-Path $root ".build\roslyn"
    $cscPath = Join-Path $cacheRoot "tasks\net472"
    if (Test-Path (Join-Path $cscPath "csc.exe")) { return $cscPath }

    if (-not $DownloadRoslyn) {
        throw "当前使用 .NET Framework 自带 MSBuild，需要 Roslyn 编译器。请加 -DownloadRoslyn 自动下载，或用 -RoslynPath 指定 csc.exe 所在目录。"
    }

    Write-Host "下载 Roslyn 编译器 $roslynVersion ..."
    New-Item -ItemType Directory -Force $cacheRoot | Out-Null
    $nupkg = Join-Path $cacheRoot "roslyn.nupkg"
    $zip = Join-Path $cacheRoot "roslyn.zip"
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$roslynVersion/microsoft.net.compilers.toolset.$roslynVersion.nupkg"
    Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing
    Copy-Item $nupkg $zip -Force
    Expand-Archive -Path $zip -DestinationPath $cacheRoot -Force
    return $cscPath
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $($msbuild.Path)"

$extraArgs = @()
if (-not $msbuild.IsFull) {
    $roslyn = Ensure-Roslyn -RequestedPath $RoslynPath
    Write-Host "Roslyn:  $roslyn"
    $extraArgs += "/p:CscToolPath=$roslyn"
    $extraArgs += "/p:CscToolExe=csc.exe"
    $extraArgs += "/p:LangVersion=latest"
}

$projects = @()
if (-not $SkipGui) { $projects += "VideoTools\VideoTools.csproj" }
$projects += "VideoTools.Automation\VideoTools.Automation.csproj"
$projects += "VideoTools.CLI\VideoTools.CLI.csproj"
$projects += "VideoTools.MCP\VideoTools.MCP.csproj"

foreach ($project in $projects) {
    $full = Join-Path $root $project
    Write-Host ""
    Write-Host "==> 构建 $project ($Configuration)"
    & $msbuild.Path $full /t:Build /p:Configuration=$Configuration /v:m /nologo @extraArgs
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$project" }
}

$appDir = Join-Path $root "VideoTools\bin\$Configuration"
Write-Host ""
Write-Host "构建完成。"
Write-Host "主程序：$appDir\VideoTools.exe"
Write-Host "CLI：   $appDir\automation\videotools.exe"
Write-Host "MCP：   $appDir\automation\videotools-mcp.exe"

# 便携包：整个 dist\VideoTools-Portable 目录可直接拷到其他 Windows 电脑或 U 盘使用
if ($Portable) {
    if (-not $FfmpegPath) {
        $ini = Join-Path $env:APPDATA ".VideoTools_250405\config.ini"
        if (Test-Path $ini) {
            $line = Get-Content $ini | Where-Object { $_ -match "^\s*FFmpegPath\s*=" } | Select-Object -First 1
            if ($line) { $FfmpegPath = ($line -split "=", 2)[1].Trim() }
        }
    }

    $dist = Join-Path $root "dist\VideoTools-Portable"
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Force $dist | Out-Null
    Copy-Item "$appDir\*" $dist -Recurse -Force

    # 清理 GAC 依赖被误复制的文件（mscorlib.dll / norm*.nlp 应使用系统自带版本）
    Remove-Item (Join-Path $dist "mscorlib.dll") -Force -ErrorAction SilentlyContinue
    Get-ChildItem $dist -Recurse -Filter "norm*.nlp" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $dist "automation") -Filter "mscorlib.dll" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $dist "automation") -Recurse -Filter "norm*.nlp" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    if ($FfmpegPath -and (Test-Path $FfmpegPath)) {
        Copy-Item $FfmpegPath (Join-Path $dist "ffmpeg.exe") -Force
        Write-Host "ffmpeg：已打包 $(Split-Path -Leaf $FfmpegPath)"
    } else {
        Write-Host "提示：未找到 ffmpeg，便携包内未附带。可用 -FfmpegPath 指定后再执行。"
    }

    # 便携配置：ffmpeg 用相对路径，输出目录留空（首次运行回退到系统“视频”目录）
    $dataDir = Join-Path $dist "data"
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $portableIni = "[Program]`r`nFFmpegPath=ffmpeg.exe`r`nGpuUse=disable`r`nGpuSelect=-1`r`nCpuMax=disable`r`nVideoSaveFloder=`r`n"
    Set-Content -Path (Join-Path $dataDir "config.ini") -Value $portableIni -Encoding UTF8

    Write-Host ""
    Write-Host "便携包：$dist"
    Write-Host "  整个文件夹拷到 U 盘或其他 Windows 10/11 电脑即可使用，配置保存在 data\config.ini。"
}

Write-Host ""
Write-Host "连接 AI 工具："
Write-Host "  & '$appDir\automation\videotools.exe' setup mcp --client claude --install"
Write-Host "  & '$appDir\automation\videotools.exe' setup mcp --client cursor"
