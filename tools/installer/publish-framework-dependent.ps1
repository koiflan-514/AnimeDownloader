<#
.SYNOPSIS
    一键打包 AnimeDownloader 为 .msi 安装包（不搭载 .NET Runtime 版本）。
    适用于目标机器已安装 .NET 10 Desktop Runtime 的用户 —— 安装包更小。

.DESCRIPTION
    流程：
      1. 前置检查：.NET SDK 与 WiX Toolset（wix）是否可用
      2. dotnet publish --self-contained false 生成发布目录（不含运行时）
      3. dotnet build tools/installer/AnimeDownloader.wixproj 生成 .msi
         （WixUI_FeatureTree：可自选安装目录、桌面快捷方式、开始菜单快捷方式）
    产物：publish\AnimeDownloader-<version>-framework-dependent-<arch>.msi

    注意：目标机器必须安装 .NET 10 Desktop Runtime 才能运行。
    https://dotnet.microsoft.com/download/dotnet/10.0

.PARAMETER Architecture
    目标架构：win-x64（默认）/ win-x86 / win-arm64

.PARAMETER Configuration
    构建配置：Release（默认）

.PARAMETER PublishDir
    发布输出目录（默认 ./publish/framework-dependent/<arch>）

.PARAMETER OutputDir
    .msi 输出目录（默认 ./publish）

.EXAMPLE
    .\tools\installer\publish-framework-dependent.ps1 -Architecture win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-x86", "win-arm64")]
    [string]$Architecture = "win-x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$PublishDir = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root   = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$app    = Join-Path $root "src\AnimeDownloader.App\AnimeDownloader.App.csproj"
$wixproj = Join-Path $PSScriptRoot "AnimeDownloader.wixproj"

if (-not $PublishDir) { $PublishDir = Join-Path $root "publish\framework-dependent\$Architecture" }
if (-not $OutputDir)  { $OutputDir  = Join-Path $root "publish" }

# ---------- 前置检查 ----------
Write-Host "== AnimeDownloader 打包（不搭载 .NET Runtime）==" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet 命令。请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "未找到 wix 命令。请安装 WiX Toolset：dotnet tool install --global wix"
}

# 读取版本（Directory.Build.props 的 <Version>）
$version = "0.1.0"
$props = Join-Path $root "Directory.Build.props"
if (Test-Path $props) {
    $m = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $version = $m.Matches[0].Groups[1].Value.Trim() }
}

# ---------- 发布 ----------
Write-Host "`n[1/2] dotnet publish ($Configuration / $Architecture / framework-dependent)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
& dotnet publish $app `
    -c $Configuration `
    -r $Architecture `
    --self-contained false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# ---------- 打 msi ----------
$archShort = $Architecture -replace '^win-', ''
$msiName = "AnimeDownloader-$version-framework-dependent-$archShort.msi"
$msiPath = Join-Path $OutputDir $msiName
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "`n[2/2] 生成 $msiName ..." -ForegroundColor Yellow
& dotnet build $wixproj `
    -c $Configuration `
    -p:Platform=$archShort `
    -p:PublishDir=$PublishDir `
    -p:Version=$version `
    -p:DeployType="framework-dependent" `
    -p:ArchShort=$archShort
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$builtMsi = Join-Path $PSScriptRoot "bin\$archShort\$Configuration\$msiName"
if (-not (Test-Path $builtMsi)) {
    $builtMsi = Get-ChildItem (Join-Path $PSScriptRoot "bin") -Recurse -Filter $msiName | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $builtMsi -or -not (Test-Path $builtMsi)) { throw "未找到生成的 .msi 文件" }
Copy-Item $builtMsi $msiPath -Force

# ---------- 报告 ----------
$sizeMB = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Host "`n✔ 打包完成：" -ForegroundColor Green
Write-Host "   $msiPath"
Write-Host "   大小：$sizeMB MB（不含 .NET Runtime）"
Write-Host "`n目标机器需安装 .NET 10 Desktop Runtime："
Write-Host "   https://dotnet.microsoft.com/download/dotnet/10.0"
