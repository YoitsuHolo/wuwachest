# build.ps1 — 编译 WuwaChest.exe（.NET Framework 4.x 自带 csc，无需安装 SDK）
# 用法：powershell -ExecutionPolicy Bypass -File .\build.ps1
#
# 图标说明：
#   app.ico  —— exe 的图标（优先用 tools\ExtractIcon.exe 从 WuwaTappo.exe 导出的那个）
#   icon.png —— 从 app.ico 里取出最大尺寸，编译时以资源名 AppIcon.png 嵌入，运行时给托盘/标题栏用
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$out = Join-Path $root 'WuwaChest.exe'
$ico = Join-Path $root 'app.ico'
$png = Join-Path $root 'icon.png'

Add-Type -AssemblyName System.Drawing

# ---------- 1. 兜底图标（只有 app.ico 缺失时才画一个）----------
function New-ChestBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 32.0
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200,121,31))
    $g.FillEllipse($bg, 1*$scale, 1*$scale, 30*$scale, 30*$scale)
    $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,248,236))
    $g.FillRectangle($fg, 9*$scale, 13*$scale, 14*$scale, 10*$scale)
    $g.FillRectangle($fg, 8*$scale, 10*$scale, 16*$scale, 4*$scale)
    $lock = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200,121,31))
    $g.FillRectangle($lock, 15*$scale, 15*$scale, 2*$scale, 7*$scale)
    $g.Dispose()
    return $bmp
}

function Save-Ico($sizes, $path) {
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-ChestBitmap $s
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,@($s, $ms.ToArray())
        $ms.Dispose()
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($item in $pngs) {
        $s = $item[0]; $data = $item[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($item in $pngs) { $bw.Write($item[1]) }
    $bw.Flush(); $fs.Close()
}

if (-not (Test-Path $ico)) {
    Save-Ico @(16, 32, 48, 256) $ico
    Write-Host "app.ico 缺失，已生成兜底图标"
}

# ---------- 2. 从 ico 里取出最大尺寸，存成 icon.png（编译时嵌入）----------
$exported = $false
try {
    $bytes = [System.IO.File]::ReadAllBytes($ico)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $bestWidth = -1; $bestOffset = 0; $bestLength = 0
    for ($i = 0; $i -lt $count; $i++) {
        $at = 6 + 16 * $i
        $w = [int]$bytes[$at]; if ($w -eq 0) { $w = 256 }
        $len = [BitConverter]::ToUInt32($bytes, $at + 8)
        $off = [BitConverter]::ToUInt32($bytes, $at + 12)
        if ($w -gt $bestWidth) { $bestWidth = $w; $bestLength = $len; $bestOffset = $off }
    }
    if ($bestLength -gt 0) {
        $data = New-Object byte[] $bestLength
        [Array]::Copy($bytes, $bestOffset, $data, 0, $bestLength)
        if ($data[0] -eq 0x89 -and $data[1] -eq 0x50 -and $data[2] -eq 0x4E -and $data[3] -eq 0x47) {
            [System.IO.File]::WriteAllBytes($png, $data)
            $exported = $true
        }
    }
} catch { $exported = $false }

if (-not $exported) {
    # ico 里不是 PNG（老式 DIB）时，让 System.Drawing 转一下
    try {
        $icon = New-Object System.Drawing.Icon($ico)
        $bmp = $icon.ToBitmap()
        $big = New-Object System.Drawing.Bitmap 256, 256
        $g = [System.Drawing.Graphics]::FromImage($big)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($bmp, 0, 0, 256, 256)
        $big.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $big.Dispose(); $bmp.Dispose(); $icon.Dispose()
        $exported = $true
    } catch { }
}

if (-not $exported) {
    $fallback = New-ChestBitmap 256
    $fallback.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $fallback.Dispose()
}
Write-Host "图标：$ico / $png"

# ---------- 3. 编译 ----------
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "找不到 csc.exe，请确认系统有 .NET Framework 4.x" }

$gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$refs = @(
    (Join-Path $gac 'UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll'),
    (Join-Path $gac 'UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll'),
    (Join-Path $gac 'WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll')
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "缺少程序集：$r" } }

$sources = Get-ChildItem -LiteralPath $src -Filter '*.cs' | Where-Object { $_.Name -notlike '_*' } | ForEach-Object { $_.FullName }

$arguments = @(
    '/nologo', '/target:winexe', '/codepage:65001', '/optimize+',
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    ('/win32icon:' + $ico),
    ('/resource:' + $png + ',AppIcon.png'),
    # 默认面板背景（没有配置 backgroundImage 时用它）
    ('/resource:' + (Join-Path $root 'default-background.jpg') + ',DefaultBackground.jpg'),
    ('/out:' + $out),
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll', '/r:Microsoft.CSharp.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
)
foreach ($r in $refs) { $arguments += ('/r:' + $r) }
$arguments += $sources

& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$exe = Get-Item -LiteralPath $out
Write-Host ("编译完成：{0}（{1:N0} 字节）" -f $exe.FullName, $exe.Length)
