# Male okienko z postepem placesindex: liczy pl_*.idx zapisane od startu procesu.
# Kwadraty bez zadnej miejscowosci (ocean) nie dostaja pliku, wiec pasek moze
# stanac ponizej 100% - koniec poznaje sie po zniknieciu procesu.
param([string]$Dir = 'O:\osm\world\packs')

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$total = (Get-ChildItem "$Dir\p_*.idx").Count
$form = New-Object Windows.Forms.Form -Property @{
    Text = 'Indeks miejsc'; Width = 380; Height = 140; TopMost = $true
    FormBorderStyle = 'FixedToolWindow'; StartPosition = 'CenterScreen'
}
$label = New-Object Windows.Forms.Label -Property @{ Left = 12; Top = 12; Width = 340; Height = 22; Font = New-Object Drawing.Font('Segoe UI', 10) }
$bar = New-Object Windows.Forms.ProgressBar -Property @{ Left = 12; Top = 42; Width = 340; Height = 24; Maximum = 100 }
$sub = New-Object Windows.Forms.Label -Property @{ Left = 12; Top = 72; Width = 340; Height = 20; ForeColor = 'DimGray' }
$form.Controls.AddRange(@($label, $bar, $sub))

$timer = New-Object Windows.Forms.Timer -Property @{ Interval = 2000 }
$timer.Add_Tick({
    $p = Get-Process placesindex -EA SilentlyContinue | Select-Object -First 1
    if (-not $p) {
        $bar.Value = 100
        $label.Text = 'Gotowe - 100 %'
        $f = Get-Item "$Dir\places.idx" -EA SilentlyContinue
        $sub.Text = if ($f) { 'places.idx: {0:N1} MB' -f ($f.Length / 1MB) } else { 'brak places.idx' }
        $timer.Stop(); return
    }
    $done = (Get-ChildItem "$Dir\pl_*.idx" -EA SilentlyContinue | Where-Object { $_.LastWriteTime -ge $p.StartTime }).Count
    $pct = [math]::Min(99, [math]::Floor(100 * $done / $total))
    $bar.Value = $pct
    $label.Text = "$pct %"
    $min = [math]::Floor(((Get-Date) - $p.StartTime).TotalMinutes)
    $sub.Text = "$done / $total kwadratow  -  $min min"
})
$timer.Start()
[void]$form.ShowDialog()
