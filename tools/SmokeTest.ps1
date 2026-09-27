param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$NamespaceName,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedTitle,

    [switch]$ExpectAuto
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message (expected: $Expected, actual: $Actual)"
    }
}

$flags = [System.Reflection.BindingFlags]'Instance,Public,NonPublic'
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$assembly = [System.Reflection.Assembly]::LoadFrom($resolvedExe)
$formType = $assembly.GetType("$NamespaceName.MainForm", $true)
$form = [System.Activator]::CreateInstance($formType, $flags, $null, @(), $null)

try {
    Assert-Equal '1.0.0.1' $assembly.GetName().Version.ToString() 'Assembly version mismatch'
    Assert-Equal '1.0.0.1' ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExe).FileVersion) 'File version mismatch'
    Assert-Equal $ExpectedTitle $form.Text 'Window title mismatch'
    Assert-Equal $ExpectedTitle $formType.GetField('_appTitle', $flags).GetValue($form).Text 'Header title mismatch'
    Assert-Equal $true ($form.Icon.Width -ge 128) 'Application icon is missing a large frame'
    Assert-Equal $true ($form.Icon.Height -ge 128) 'Application icon is missing a large frame'

    $injector = $assembly.GetType("$NamespaceName.PinballSiteInjector", $false)
    Assert-Equal ([bool]$ExpectAuto) ($null -ne $injector) 'General/auto separation mismatch'

    $layoutUi = $formType.GetMethod('LayoutUi', $flags)
    $layoutUi.Invoke($form, @()) | Out-Null
    $surface = $formType.GetField('_surface', $flags).GetValue($form)
    Assert-Equal ([System.Drawing.Color]::FromArgb(237, 232, 255)) $surface.TopColor 'Top background color mismatch'
    Assert-Equal ([System.Drawing.Color]::FromArgb(218, 243, 255)) $surface.BottomColor 'Bottom background color mismatch'
    Assert-Equal ([System.Drawing.Color]::FromArgb(73, 53, 111)) $formType.GetField('_appTitle', $flags).GetValue($form).ForeColor 'Header color mismatch'
    $logo = $formType.GetField('_logo', $flags).GetValue($form)
    $headerTitle = $formType.GetField('_appTitle', $flags).GetValue($form)
    $headerSubtitle = $formType.GetField('_appSubtitle', $flags).GetValue($form)
    Assert-Equal 68 $logo.Width 'Header logo width mismatch'
    Assert-Equal 68 $logo.Height 'Header logo height mismatch'
    Assert-Equal 0 $logo.FillColor.A 'Header logo square fill is visible'
    Assert-Equal 0 $logo.BorderColor.A 'Header logo square border is visible'
    Assert-Equal 2 ($headerSubtitle.Left - $headerTitle.Left) 'Header optical alignment offset mismatch'
    Assert-Equal ($headerTitle.Top - $logo.Top) ($logo.Bottom - $headerSubtitle.Bottom) 'Header text group is not vertically centered with the logo'
    Assert-Equal ([System.Drawing.ContentAlignment]::MiddleLeft) $headerTitle.TextAlign 'Header title alignment mismatch'
    Assert-Equal ([System.Drawing.ContentAlignment]::MiddleLeft) $headerSubtitle.TextAlign 'Header subtitle alignment mismatch'

    $seedMethod = $formType.GetMethods($flags) |
        Where-Object { $_.Name -eq 'SeedManyPreviewRows' -and $_.GetParameters().Count -eq 1 } |
        Select-Object -First 1
    $seedMethod.Invoke($form, @(400)) | Out-Null
    $formType.GetMethod('RefreshCounts', $flags).Invoke($form, @()) | Out-Null
    $entryList = $formType.GetField('_entryList', $flags).GetValue($form)
    $compactRowCount = $entryList.Controls.Count
    $form.ClientSize = [System.Drawing.Size]::new($form.ClientSize.Width, 1350)
    $layoutUi.Invoke($form, @()) | Out-Null
    $expandedRowCount = $entryList.Controls.Count
    $expectedVisibleRows = [Math]::Ceiling($entryList.ClientSize.Height / $entryList.Controls[0].Height)
    Assert-Equal $true ($expandedRowCount -gt $compactRowCount) 'The row pool did not grow with the window height'
    Assert-Equal $true ($expandedRowCount -ge $expectedVisibleRows) 'The expanded list cannot fill its visible height'
    Assert-Equal $true ($expandedRowCount -le ($expectedVisibleRows + 1)) 'The expanded list created too many row controls'
    $form.ClientSize = [System.Drawing.Size]::new($form.ClientSize.Width, 680)
    $layoutUi.Invoke($form, @()) | Out-Null
    Assert-Equal $expandedRowCount $entryList.Controls.Count 'The row pool was rebuilt while shrinking the window'
    $form.ClientSize = [System.Drawing.Size]::new(1630, 900)
    $layoutUi.Invoke($form, @()) | Out-Null
    $firstRow = $entryList.Controls[0]
    $rowType = $firstRow.GetType()
    $editFrame = $rowType.GetField('_editFrame', $flags).GetValue($firstRow)
    $metaLabel = $rowType.GetField('_metaLabel', $flags).GetValue($firstRow)
    Assert-Equal 10 ($metaLabel.Left - $editFrame.Right) 'The name field does not fill the space before metadata'
    Assert-Equal $true ($editFrame.Width -gt 500) 'The name field is still capped at the old fixed width'
    Assert-Equal ([System.Drawing.ContentAlignment]::MiddleLeft) $metaLabel.TextAlign 'The metadata is not aligned next to the name field'

    $calculateCoins = $formType.GetMethod('CalculateCoins', $flags)
    Assert-Equal 1 $calculateCoins.Invoke($form, @(100)) '100-count coin calculation failed'
    Assert-Equal 11 $calculateCoins.Invoke($form, @(1000)) '1000-count bonus calculation failed'

    $giftSourceType = $assembly.GetType("$NamespaceName.GiftSource", $true)
    $starBalloon = [System.Enum]::Parse($giftSourceType, 'StarBalloon')
    $entries = $formType.GetField('_entries', $flags).GetValue($form)
    $pending = $formType.GetField('_pending', $flags).GetValue($form)
    $handleGift = $formType.GetMethod('HandleBalloonGift', $flags)
    $handleChat = $formType.GetMethod('HandleChatMessage', $flags)
    $nicknameMode = $formType.GetField('_nicknamePinballMode', $flags)

    $nicknameMode.SetValue($form, $true)
    $entryCount = $entries.Count
    $pendingCount = $pending.Count
    $handleGift.Invoke($form, @($starBalloon, 'instant-user', 100)) | Out-Null
    $lastEntry = $entries[$entries.Count - 1]
    Assert-Equal ($entryCount + 1) ($entries.Count) 'Nickname mode did not add the gift immediately'
    Assert-Equal $pendingCount ($pending.Count) 'Nickname mode incorrectly queued a pending gift'
    Assert-Equal 'instant-user' $lastEntry.PinballName 'Nickname mode used the wrong pinball name'

    $nicknameMode.SetValue($form, $false)
    $entryCount = $entries.Count
    $pendingCount = $pending.Count
    $handleGift.Invoke($form, @($starBalloon, 'chat-user', 100)) | Out-Null
    Assert-Equal $entryCount ($entries.Count) 'Content mode added a gift before chat'
    Assert-Equal ($pendingCount + 1) ($pending.Count) 'Content mode did not queue the gift'
    $handleChat.Invoke($form, @('chat-user', 'chat-message')) | Out-Null
    $lastEntry = $entries[$entries.Count - 1]
    Assert-Equal ($entryCount + 1) ($entries.Count) 'Content mode did not add the matching chat'
    Assert-Equal $pendingCount ($pending.Count) 'Content mode did not consume the pending gift'
    Assert-Equal 'chat-message' $lastEntry.PinballName 'Content mode used the wrong pinball name'

    $thresholdInput = $formType.GetField('_thresholdInput', $flags).GetValue($form)
    $exactMode = $formType.GetField('_exactMode', $flags)
    $thresholdInput.Value = 200
    $exactMode.SetValue($form, $true)
    $nicknameMode.SetValue($form, $true)
    foreach ($count in 100, 300) {
        $before = $entries.Count
        $handleGift.Invoke($form, @($starBalloon, "rejected-$count", $count)) | Out-Null
        Assert-Equal $before $entries.Count "Exact mode accepted non-multiple $count"
    }
    foreach ($case in @(@(200, 1), @(400, 2), @(2000, 11))) {
        $before = $entries.Count
        $handleGift.Invoke($form, @($starBalloon, "multiple-$($case[0])", $case[0])) | Out-Null
        Assert-Equal ($before + 1) $entries.Count "Exact mode rejected multiple $($case[0])"
        Assert-Equal $case[1] $entries[$entries.Count - 1].CoinCount "Wrong coin count for $($case[0])"
    }
    $exactMode.SetValue($form, $false)
    $before = $entries.Count
    $handleGift.Invoke($form, @($starBalloon, 'at-least-300', 300)) | Out-Null
    Assert-Equal ($before + 1) $entries.Count 'At-least mode rejected 300'
    Assert-Equal 1 $entries[$entries.Count - 1].CoinCount 'At-least mode coin count changed'
    $exactMode.SetValue($form, $true)
    $nicknameMode.SetValue($form, $false)
    $before = $pending.Count
    $handleGift.Invoke($form, @($starBalloon, 'chat-multiple', 400)) | Out-Null
    Assert-Equal ($before + 1) $pending.Count 'Content mode did not queue exact multiple'
    $before = $entries.Count
    $handleChat.Invoke($form, @('chat-multiple', 'multiple-message')) | Out-Null
    Assert-Equal ($before + 1) $entries.Count 'Content mode did not collect exact multiple'
    Assert-Equal 2 $entries[$entries.Count - 1].CoinCount 'Content mode lost exact-multiple coins'

    $source = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $SourcePath), [System.Text.Encoding]::UTF8)
    foreach ($command in 18, 33, 87, 121) {
        Assert-Equal $true $source.Contains("serviceCommand == $command") "Packet command $command is missing"
    }
    Assert-Equal $true $source.Contains('https://gyeon-ai.github.io/GyeolheePinball-Web/') 'Gyeolhee web URL is missing'
    Assert-Equal $false $source.Contains('TayoPinball') 'Tayo branding remains in source'
    $koreanTayo = -join ([char]0xD0C0, [char]0xC694)
    Assert-Equal $false $source.Contains($koreanTayo) 'Korean Tayo branding remains in source'
    Assert-Equal $false $source.Contains('dan259') 'Old preview SOOP ID remains in source'

    $resources = @($assembly.GetManifestResourceNames())
    Assert-Equal $true ($resources -contains 'GyeolheePinballLogo') 'Embedded logo is missing'
    Assert-Equal $true ($resources -contains 'GyeolheePinballIcon') 'Embedded icon is missing'

    Write-Output "PASS: $ExpectedTitle, palette, resources, responsive 400-item list, exact-multiple coin rules, packet branches"
}
finally {
    $form.Dispose()
}
