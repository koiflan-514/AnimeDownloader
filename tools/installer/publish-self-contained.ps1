<#
.SYNOPSIS
    一键打包 AnimeDownloader 为 .msi 安装包（自带 .NET Runtime 版本）。
    适用于目标机器未安装 .NET 的用户 —— 安装后开箱即用，无需任何运行时。

.DESCRIPTION
    流程：
      1. 前置检查：.NET SDK 与 WiX Toolset（wix）是否可用
      2. dotnet publish --self-contained true -r <arch> 生成完整发布目录
      3. dotnet build tools/installer/AnimeDownloader.wixproj 生成 .msi
         （中文安装向导：许可协议、自定义安装目录、完成后显示卸载指引）
    产物：publish\AnimeDownloader-<version>-self-contained-<arch>.msi

.PARAMETER Architecture
    目标架构：win-x64（默认）/ win-x86 / win-arm64

.PARAMETER Configuration
    构建配置：Release（默认）

.PARAMETER PublishDir
    发布输出目录（默认 ./publish/self-contained/<arch>）

.PARAMETER OutputDir
    .msi 输出目录（默认 ./publish）

.EXAMPLE
    .\tools\installer\publish-self-contained.ps1 -Architecture win-x64
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

if (-not $PublishDir) { $PublishDir = Join-Path $root "publish\self-contained\$Architecture" }
if (-not $OutputDir)  { $OutputDir  = Join-Path $root "publish" }

# ---------- 前置检查 ----------
Write-Host "== AnimeDownloader 打包（自带 .NET Runtime）==" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet 命令。请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "未找到 wix 命令。请安装 WiX Toolset：dotnet tool install --global wix"
}

# 读取版本（Directory.Build.props 的 <Version>）
$version = "0.2.0"
$props = Join-Path $root "Directory.Build.props"
if (Test-Path $props) {
    $m = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>'
    if ($m) { $version = $m.Matches[0].Groups[1].Value.Trim() }
}

# ---------- 发布 ----------
Write-Host "`n[1/2] dotnet publish ($Configuration / $Architecture / self-contained)..." -ForegroundColor Yellow
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
& dotnet publish $app `
    -c $Configuration `
    -r $Architecture `
    --self-contained true `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# ---------- 打 msi ----------
$archShort = $Architecture -replace '^win-', ''
$msiName = "AnimeDownloader-$version-self-contained-$archShort.msi"
$msiPath = Join-Path $OutputDir $msiName
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "`n[2/2] 生成 $msiName ..." -ForegroundColor Yellow
# 清理上一次打包的中间产物，避免增量构建复用旧输出
foreach ($dir in @((Join-Path $PSScriptRoot "obj"), (Join-Path $PSScriptRoot "bin"))) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
# wixproj 的 OutputName 已含版本/类型/架构，此处直接构建并复制到 OutputDir
& dotnet build $wixproj `
    -c $Configuration `
    -p:Platform=$archShort `
    -p:PublishDir=$PublishDir `
    -p:Version=$version `
    -p:DeployType="self-contained" `
    -p:ArchShort=$archShort
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$builtMsi = Get-ChildItem (Join-Path $PSScriptRoot "bin") -Recurse -Filter $msiName | Select-Object -First 1 -ExpandProperty FullName
if (-not $builtMsi -or -not (Test-Path $builtMsi)) { throw "未找到生成的 .msi 文件" }
Copy-Item $builtMsi $msiPath -Force

# ---------- 报告 ----------
$sizeMB = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Host "`n✔ 打包完成：" -ForegroundColor Green
Write-Host "   $msiPath"
Write-Host "   大小：$sizeMB MB（含 .NET Runtime）"
Write-Host "`n在未安装 .NET 的机器上运行该 .msi 即可安装使用。"
