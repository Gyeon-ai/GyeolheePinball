param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$NamespaceName,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int]$ClientWidth = 1093,
    [int]$ClientHeight = 688,
    [int]$EntryCount = 0,
    [switch]$FailOnIssues
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $ExePath).Path)
$type = $assembly.GetType("$NamespaceName.MainForm", $true)
$form = [Activator]::CreateInstance($type, $flags, $null, @(), $null)
$issues = New-Object 'System.Collections.Generic.List[object]'
$metrics = New-Object 'System.Collections.Generic.List[object]'
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

function Field($Object, [string]$Name) {
    $field = $Object.GetType().GetField($Name, $flags)
    if ($null -ne $field) { return ,$field.GetValue($Object) }
    return $null
}

function Save-Rendering($Control, [string]$Name) {
    if ($Control.Width -le 0 -or $Control.Height -le 0) { return }
    $bitmap = New-Object Drawing.Bitmap($Control.Width, $Control.Height)
    try {
        $Control.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height)))
        $bitmap.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

try {
    # Construct a separate test form; no broadcast connection or user collection is used.
    $form.ShowInTaskbar = $false
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-20000, -20000)
    $form.ClientSize = New-Object Drawing.Size($ClientWidth, $ClientHeight)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    if ($EntryCount -gt 0) {
        $entries = Field $form '_entries'
        $entryType = $assembly.GetType("$NamespaceName.CollectedEntry", $true)
        for ($i = 1; $i -le $EntryCount; $i++) {
            $entry = [Activator]::CreateInstance($entryType, $true)
            $entry.Nickname = if ($i -eq 1) { 'LongNickname_ABCDEFGHIJKLMNOPQRSTUVWXYZ' } else { "Viewer$i" }
            $entry.PinballName = if ($i -eq 1) { 'Long entry title ABCDEFGHIJKLMNOPQRSTUVWXYZ 1234567890 ABCDEFGHIJKLMNOPQRSTUVWXYZ 1234567890' } else { "Entry $i" }
            $entry.BalloonCount = 100
            $entry.CoinCount = if ($i -eq 1) { 9999 } else { 1 }
            $entry.ReceivedAt = '12:34:56'
            $entries.Add($entry)
        }
        $type.GetMethod('RefreshCounts', $flags).Invoke($form, @()) | Out-Null
        $type.GetMethod('RefreshPinballText', $flags, $null, [type[]]@([bool]), $null).Invoke($form, @($false)) | Out-Null
    }
    $type.GetMethod('LayoutUi', $flags).Invoke($form, @()) | Out-Null
    [Windows.Forms.Application]::DoEvents()
    $graphics = $form.CreateGraphics()
    try {
        foreach ($field in $type.GetFields($flags)) {
            $control = $field.GetValue($form)
            if ($control -isnot [Windows.Forms.Control] -or !$control.Visible) { continue }
            if ($control -isnot [Windows.Forms.Label] -and $control.GetType().Name -ne 'RoundButton') { continue }
            if ([string]::IsNullOrWhiteSpace($control.Text)) { continue }
            $size = [Windows.Forms.TextRenderer]::MeasureText($graphics, $control.Text, $control.Font, [Drawing.Size]::Empty,
                [Windows.Forms.TextFormatFlags]'NoPadding,SingleLine')
            $wrapped = [Windows.Forms.TextRenderer]::MeasureText($graphics, $control.Text, $control.Font,
                (New-Object Drawing.Size([Math]::Max(1, $control.Width - $control.Padding.Horizontal), 0)), [Windows.Forms.TextFormatFlags]'WordBreak')
            $metric = [pscustomobject]@{ Field=$field.Name; Text=$control.Text; Bounds=$control.Bounds.ToString(); Width=$control.Width; Height=$control.Height; TextWidth=$size.Width; TextHeight=$size.Height; WrappedHeight=$wrapped.Height; Font=$control.Font.ToString() }
            $metrics.Add($metric)
            if ($size.Height -gt $control.Height -or ($control -is [Windows.Forms.Label] -and $wrapped.Height -gt $control.Height)) {
                $issues.Add([pscustomobject]@{Kind='TextHeight';Field=$field.Name;Details=$metric})
            }
            if ($control.GetType().Name -eq 'RoundButton') {
                $glyph = $control.GetType().GetProperty('Glyph')
                $extra = if ($glyph -and $glyph.GetValue($control, $null).ToString() -ne 'None') { 25 } else { 0 }
                if ($size.Width + $extra -gt $control.Width - 6) {
                    $issues.Add([pscustomobject]@{Kind='ButtonWidth';Field=$field.Name;Details=$metric})
                }
            }
            $parentBounds = if ($control.Parent -is [Windows.Forms.ScrollableControl] -and $control.Parent.AutoScroll) { $control.Parent.DisplayRectangle } else { $control.Parent.ClientRectangle }
            if ($control.Parent -and !$parentBounds.Contains($control.Bounds)) {
                $issues.Add([pscustomobject]@{Kind='OutsideParent';Field=$field.Name;Details=$metric})
            }
        }
    } finally { $graphics.Dispose() }

    $fields = @($type.GetFields($flags) | Where-Object { $_.GetValue($form) -is [Windows.Forms.Control] })
    for ($i = 0; $i -lt $fields.Count; $i++) {
        $a = $fields[$i].GetValue($form)
        if (!$a.Visible -or $fields[$i].Name -eq '_toast') { continue }
        for ($j = $i + 1; $j -lt $fields.Count; $j++) {
            $b = $fields[$j].GetValue($form)
            if (!$b.Visible -or $a.Parent -ne $b.Parent -or $fields[$j].Name -eq '_toast') { continue }
            $overlap = [Drawing.Rectangle]::Intersect($a.Bounds, $b.Bounds)
            if ($overlap.Width -gt 2 -and $overlap.Height -gt 2) {
                $issues.Add([pscustomobject]@{Kind='Overlap';Field="$($fields[$i].Name),$($fields[$j].Name)";Details=$overlap.ToString()})
            }
        }
    }
    Save-Rendering $form 'main'
    foreach ($name in '_setupCard','_collectionCard','_pinballCard') { Save-Rendering (Field $form $name) $name }
    $popup = Field $form '_targetPopup'
    if ($popup) {
        $type.GetMethod('ShowTargetPopup', $flags).Invoke($form, @()) | Out-Null
        [Windows.Forms.Application]::DoEvents()
        Save-Rendering $form 'popup-context'
        Save-Rendering $popup 'popup'
    }
    if ($EntryCount -eq 0) {
        foreach ($dialogName in 'ClearEntriesDialog','InputRequiredDialog','UpdatePromptDialog','UpdateProgressDialog') {
            $dialogType = $assembly.GetType("$NamespaceName.$dialogName", $false)
            if (!$dialogType) { continue }
            $arguments = if ($dialogName -eq 'UpdatePromptDialog') { @([Version]'1.4.5.0', 'Update test notes') }
                elseif ($dialogName -eq 'UpdateProgressDialog') { @() }
                else { @($form.Font, (Field $form '_text'), (Field $form '_muted'), (Field $form '_purple'), (Field $form '_line')) }
            $dialog = [Activator]::CreateInstance($dialogType, $flags, $null, $arguments, $null)
            try {
                $dialog.StartPosition = [Windows.Forms.FormStartPosition]::Manual
                $dialog.Location = New-Object Drawing.Point(-20000, -20000)
                $dialog.ShowInTaskbar = $false
                $dialog.Show()
                [Windows.Forms.Application]::DoEvents()
                Save-Rendering $dialog $dialogName
                foreach ($child in $dialog.Controls) {
                    if ($child -isnot [Windows.Forms.Label] -and $child -isnot [Windows.Forms.Button] -and $child.GetType().Name -ne 'RoundButton') { continue }
                    $size = [Windows.Forms.TextRenderer]::MeasureText($child.Text, $child.Font,
                        (New-Object Drawing.Size([Math]::Max(1, $child.Width - $child.Padding.Horizontal), 0)), [Windows.Forms.TextFormatFlags]'WordBreak')
                    if ($size.Height -gt $child.Height) {
                        $issues.Add([pscustomobject]@{Kind='DialogTextHeight';Field=$dialogName;Details="$($child.Text): required=$($size.Height), actual=$($child.Height)"})
                    }
                }
            } finally { $dialog.Close(); $dialog.Dispose() }
        }
    }
    $rowMetrics = $null
    $entryList = Field $form '_entryList'
    if ($entryList.Controls.Count -gt 0) {
        $row = $entryList.Controls[0]
        $editFrame = Field $row '_editFrame'
        $nameBox = Field $row '_nameBox'
        $rowMetrics = [pscustomobject]@{PoolCount=$entryList.Controls.Count;RowHeight=$row.Height;EditBounds=$editFrame.Bounds.ToString();NameBounds=$nameBox.Bounds.ToString();NamePreferredHeight=$nameBox.PreferredHeight}
        if ($type.GetMethod('PositionEntryRows', $flags)) {
            $topGap = $nameBox.Top - $editFrame.Top
            $bottomGap = $editFrame.Bottom - $nameBox.Bottom
            if ([Math]::Abs($topGap - $bottomGap) -gt 1) {
                $issues.Add([pscustomobject]@{Kind='RowEditorAlignment';Field='_nameBox';Details="top=$topGap bottom=$bottomGap"})
            }
        }
    }
    $report = [pscustomobject]@{Exe=$ExePath;Client=$form.ClientSize.ToString();Entries=$EntryCount;Rows=$rowMetrics;Issues=@($issues.ToArray());Metrics=@($metrics.ToArray())}
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'layout.json') -Encoding UTF8
    Write-Output "$NamespaceName $($form.ClientSize) entries=$EntryCount candidates=$($issues.Count)"
    foreach ($issue in $issues) { Write-Output "  $($issue.Kind): $($issue.Field)" }
    if ($FailOnIssues -and $issues.Count -gt 0) { throw "Layout candidates require review: $($issues.Count)" }
} finally {
    $form.Close()
    $form.Dispose()
}
