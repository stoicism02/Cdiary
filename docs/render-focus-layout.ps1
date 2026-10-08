Add-Type -AssemblyName System.Drawing
$canvas = New-Object System.Drawing.Bitmap 1480,980
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$ink='#725D42'; $muted='#99886E'; $paper='#FFFBEA'; $mint='#19BCAE'; $line='#DDD3B9'
function Brush($hex) { New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex)) }
function Box($x,$y,$w,$h,$r,$fill,$stroke='') {
 $p=New-Object System.Drawing.Drawing2D.GraphicsPath
 $d=$r*2
 $p.AddArc($x,$y,$d,$d,180,90);$p.AddArc(($x+$w-$d),$y,$d,$d,270,90)
 $p.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90);$p.AddArc($x,($y+$h-$d),$d,$d,90,90);$p.CloseFigure()
 $b=Brush $fill;$g.FillPath($b,$p);$b.Dispose()
 if($stroke){$pen=New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($stroke)),1.5;$g.DrawPath($pen,$p);$pen.Dispose()};$p.Dispose()
}
function Text($s,$x,$y,$size=16,$color=$ink,$bold=$false) {
 $style=[System.Drawing.FontStyle]::Regular;if($bold){$style=[System.Drawing.FontStyle]::Bold}
 $f=New-Object System.Drawing.Font 'Microsoft YaHei UI',$size,$style,([System.Drawing.GraphicsUnit]::Pixel)
 $b=Brush $color;$g.DrawString($s,$f,$b,[float]$x,[float]$y);$b.Dispose();$f.Dispose()
}
function Rule($x,$y,$w){$pen=New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($line)),1;$g.DrawLine($pen,$x,$y,($x+$w),$y);$pen.Dispose()}
function Button($s,$x,$y,$w,$primary=$false){$fill=$paper;$edge=$line;$fg=$ink;if($primary){$fill=$mint;$edge=$mint;$fg='#FFFFFF'};Box $x ($y+3) $w 38 17 '#D0C6AA';Box $x $y $w 38 17 $fill $edge;Text $s ($x+17) ($y+8) 15 $fg $true}
$g.Clear([System.Drawing.ColorTranslator]::FromHtml('#F0EDE2'))
Text '小岛日记 · 专注功能排版草图' 40 22 28 $ink $true
Text '布局预览，所有计时与统计均为示例数据' 42 59 15 $muted

Box 40 102 910 668 24 $paper $line
Box 52 115 61 642 20 '#F1EBD7'
Text '✿' 66 128 29 $mint $true
Text '待办' 65 202 16
Text '日历' 65 255 16
Box 57 304 51 48 14 '#DDF5ED'
Text '专注' 65 319 16 '#177A70' $true
Text '搜索' 65 381 16
Text '设置' 65 707 16
Text '专注' 140 126 25 $ink $true
Text '今天，慢慢做好手头这一件事。' 141 166 15 $muted
Text '10 月 6 日  星期二' 744 140 15 $muted
Rule 141 201 781

Box 143 219 478 324 22 '#F5F0DB'
Box 163 236 164 34 15 '#FFFBEA' $line
Box 166 239 79 28 13 '#DDF5ED'
Text '番茄钟' 179 244 14 '#177A70' $true
Text '正计时' 259 244 14
Text '第 3 轮 / 4 轮' 474 246 13 $muted
Text '学习数据库索引' 290 283 21 $ink $true
Text '18:42' 285 344 66 $ink $true
Text '本轮已专注 6 分 18 秒' 298 426 15 $muted
Button '暂停' 270 468 106 $true
Button '结束' 390 468 106

Text '今日投入' 653 227 18 $ink $true
Text '1 小时 12 分' 653 261 30 $ink $true
Text '完成 2 轮番茄' 655 305 14 $muted
Rule 654 343 240
Text '学习数据库索引' 654 364 15
Text '42 分' 847 365 14 $muted
Box 655 396 238 7 3 '#EBE3CD'
Box 655 396 158 7 3 '#70C5AE'
Text '整理项目笔记' 654 424 15
Text '30 分' 847 425 14 $muted
Box 655 456 238 7 3 '#EBE3CD'
Box 655 456 113 7 3 '#B3CDA0'
Text '任务汇总包含当前正在进行的专注' 655 500 12 $muted

Text '专注记录' 145 571 18 $ink $true
Text '今天  ▾' 847 574 14 $muted
Text '时间' 145 613 13 $muted
Text '任务' 305 613 13 $muted
Text '投入' 687 613 13 $muted
Text '结果' 803 613 13 $muted
Rule 145 641 766
Text '14:00 — 14:25' 145 657 14
Text '学习数据库索引' 305 657 14
Text '25 分钟' 685 657 14
Text '完成一轮' 803 657 14 '#177A70'
Rule 145 689 766
Text '13:20 — 13:31' 145 708 14
Text '学习数据库索引' 305 708 14
Text '11 分钟' 685 708 14
Text '提前结束' 803 708 14 $muted

Box 998 102 438 668 24 $paper $line
Text '任务便签' 1020 125 22 $ink $true
Text '置顶    ×' 1329 133 15 $muted
Rule 1020 173 393
Box 1018 191 396 55 16 '#FFFDED' $line
Text '学习数据库索引' 1034 207 19 $ink $true
Box 1018 261 396 45 15 '#E6F5E9'
Text '专注中' 1033 273 13 '#177A70'
Text '18:42' 1103 268 23 '#177A70' $true
Text '暂停   结束' 1291 274 14
Text '正文 ▾   B   I   ☑   目录   ⋯' 1032 324 15 $muted
Rule 1019 356 394
Text 'B+ 树的查询过程' 1033 381 23 $ink $true
Text '先找到索引，再定位到对应的数据页。' 1033 431 16
Text '今天需要理解：' 1033 478 16
Text '□ 聚簇索引与非聚簇索引' 1033 517 16
Text '□ 回表为什么会增加查询成本' 1033 553 16
Text '□ 哪些情况会导致索引失效' 1033 589 16
Text '│' 1031 631 22 $mint
Text '自动保存' 1030 734 12 $muted
Text '只添加一条计时栏，正文仍是主要空间' 1005 787 16 $muted

Text '未开始时' 47 800 17 $ink $true
Box 43 835 514 49 17 $paper $line
Text '累计专注 42 分' 59 851 15 $muted
Button '开始专注' 411 840 130 $true
Text '小窗口时' 597 800 17 $ink $true
Box 593 835 345 49 17 '#E6F5E9'
Text '18:42' 610 843 26 '#177A70' $true
Text '暂停   结束' 819 852 14
Text '记录与设置放在专注页，便签保持紧凑。' 1004 840 17
Text '到时轻声提醒，不自动抢焦点。' 1004 871 16 $muted
Text '此图用于确认排版；尚未接入正式界面。' 44 931 15 $muted
$target=Join-Path $PSScriptRoot 'focus-layout.png'
$canvas.Save($target,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose();$canvas.Dispose()
Write-Output $target
