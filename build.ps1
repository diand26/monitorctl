<#
  build.ps1 —— 编译 monitorctl（零 SDK 依赖）

  不装 dotnet SDK / NuGet / 第三方库：直接调用 Windows 自带的
  .NET Framework 4.8 C# 编译器（csc.exe）编译 src\*.cs。
  产物：dist\monitorctl.exe（单文件控制台程序：CLI + 托盘 + 设置窗口 + 手机页都在里面）

  要求：Windows 10 1809+ / Windows 11（自带 .NET Framework 4.8）
  用法：
    .\build.ps1          只编译
    .\build.ps1 -Test    编译后跑 init / list / get 冒烟（需要接一台支持 DDC/CI 的显示器）

  说明：
    - 编译前会结束「正在占用目标 exe」的那个 monitorctl；别的路径下的实例不动。
      Windows 上正在运行的 exe 不能被覆盖，否则 csc 报 CS0016。
    - 源码是 UTF-8（无 BOM），所以显式加 /codepage:65001，
      避免换到别的系统代码页时中文变乱码。
    - /langversion:5 把语言级别锁在 C# 5：代码只用了 C# 5 语法，
      这样连 VS2013 时代的 csc 也能编。
#>
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'monitorctl.exe'

# --- 1. 结束正在占用目标 exe 的实例（只按完整路径匹配，不误杀其它拷贝） ---
$running = @()
foreach ($p in @(Get-Process -Name monitorctl -ErrorAction SilentlyContinue)) {
    $pPath = $null
    try { $pPath = $p.Path } catch { }
    if ($pPath -and ($pPath -ieq $out)) { $running += $p }
}
if ($running.Count -gt 0) {
    Write-Host ("检测到正在运行的 {0}（PID {1}），先结束它 ..." -f $out, (($running | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

# --- 2. 找 csc.exe：Framework64 → 32 位 Framework → Visual Studio / Build Tools ---
$candidates = New-Object System.Collections.Generic.List[string]
$candidates.Add((Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'))
$candidates.Add((Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'))

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    try {
        $vsRoot = @(& $vswhere -latest -products * -property installationPath) | Select-Object -First 1
        if ($vsRoot) {
            $candidates.Add((Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'))
            $candidates.Add((Join-Path $vsRoot 'MSBuild\15.0\Bin\Roslyn\csc.exe'))
        }
    } catch { }
}

$csc = $null
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) { $csc = $c; break }
}

if (-not $csc) {
    Write-Host '找不到 C# 编译器（csc.exe）。' -ForegroundColor Red
    Write-Host 'monitorctl 不需要 dotnet SDK，只需要 .NET Framework 4.8：' -ForegroundColor Yellow
    Write-Host '  1) Windows 10 1809+ / Windows 11 默认自带 .NET Framework 4.8，先确认系统没被精简；'
    Write-Host '  2) 缺失时安装 .NET Framework 4.8：https://dotnet.microsoft.com/download/dotnet-framework/net48'
    Write-Host '  3) 或安装 Visual Studio Build Tools（勾选 .NET 桌面生成工具）后重试。'
    exit 1
}
Write-Host ("编译器：{0}" -f $csc) -ForegroundColor DarkGray

# --- 3. 编译 ---
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$files = @(Get-ChildItem -Path $src -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($files.Count -eq 0) {
    Write-Host ("src 下没有 .cs 文件：{0}" -f $src) -ForegroundColor Red
    exit 1
}

Write-Host ("编译 {0} 个源文件 ..." -f $files.Count) -ForegroundColor Cyan
& $csc /nologo /target:exe /platform:anycpu /optimize+ /warn:4 /langversion:5 /codepage:65001 `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /out:$out $files

if ($LASTEXITCODE -ne 0) {
    Write-Host ("编译失败（csc 退出码 {0}）" -f $LASTEXITCODE) -ForegroundColor Red
    exit $LASTEXITCODE
}

$item = Get-Item -LiteralPath $out
$hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLower()
Write-Host ("OK -> {0}  ({1:N0} 字节)" -f $out, $item.Length) -ForegroundColor Green
Write-Host ("SHA-256: {0}" -f $hash)

# --- 4. 可选冒烟（需要真实显示器，CI 默认不跑） ---
if ($Test) {
    Write-Host "`n--- init ---" -ForegroundColor Cyan
    & $out init
    Write-Host "`n--- list ---" -ForegroundColor Cyan
    & $out list
    Write-Host "`n--- get ---" -ForegroundColor Cyan
    & $out get
}
