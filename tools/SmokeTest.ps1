param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$NamespaceName,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedTitle,

    [switch]$ExpectAuto,

    [string]$ExpectedVersion = '1.0.0.2'
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
    Assert-Equal $ExpectedVersion $assembly.GetName().Version.ToString() 'Assembly version mismatch'
    Assert-Equal $ExpectedVersion ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExe).FileVersion) 'File version mismatch'
    Assert-Equal $ExpectedTitle $form.Text 'Window title mismatch'
    Assert-Equal $true ($null -ne $formType.GetField('_stage', $flags).GetValue($form)) 'Main drawing surface is missing'
    Assert-Equal $true ($form.Icon.Width -ge 128) 'Application icon is missing a large frame'
    Assert-Equal $true ($form.Icon.Height -ge 128) 'Application icon is missing a large frame'

    $injector = $assembly.GetType("$NamespaceName.PinballSiteInjector", $false)
    Assert-Equal ([bool]$ExpectAuto) ($null -ne $injector) 'General/auto separation mismatch'

    $layoutUi = $formType.GetMethod('LayoutUi', $flags)
    $layoutUi.Invoke($form, @()) | Out-Null
    # Arcade dot palette of the Gyeolhee web board.
    $colors = $assembly.GetType("$NamespaceName.ArcadeColors", $true)
    $staticFlags = [System.Reflection.BindingFlags]'Static,Public,NonPublic'
    Assert-Equal ([System.Drawing.Color]::FromArgb(0x2e, 0x2a, 0x5c).ToArgb()) $colors.GetField('Background', $staticFlags).GetValue($null).ToArgb() 'Background color mismatch'
    Assert-Equal ([System.Drawing.Color]::FromArgb(0x5f, 0x53, 0xb8).ToArgb()) $colors.GetField('Header', $staticFlags).GetValue($null).ToArgb() 'Header color mismatch'
    Assert-Equal ([System.Drawing.Color]::FromArgb(0xa9, 0xd8, 0xff).ToArgb()) $colors.GetField('Accent', $staticFlags).GetValue($null).ToArgb() 'Accent color mismatch'

    # The list is drawn directly: only visible rows are painted and no per-row controls are created.
    $seedMethod = $formType.GetMethods($flags) |
        Where-Object { $_.Name -eq 'SeedManyPreviewRows' -and $_.GetParameters().Count -eq 1 } |
        Select-Object -First 1
    $seedMethod.Invoke($form, @(400)) | Out-Null
    $formType.GetMethod('RefreshCounts', $flags).Invoke($form, @()) | Out-Null
    $stage = $formType.GetField('_stage', $flags).GetValue($form)
    $controlCount = $stage.Controls.Count
    Assert-Equal 400 $formType.GetField('_visibleEntries', $flags).GetValue($form).Count 'The 400-item list is not fully listed'
    Assert-Equal $true ($controlCount -lt 20) 'Row controls were created for list items'
    foreach ($size in @(@(1280, 1350), @(1280, 680), @(1630, 900))) {
        $form.ClientSize = [System.Drawing.Size]::new($size[0], $size[1])
        $layoutUi.Invoke($form, @()) | Out-Null
        Assert-Equal $controlCount $stage.Controls.Count 'Resizing created or removed controls'
        $bitmap = New-Object System.Drawing.Bitmap($form.ClientSize.Width, $form.ClientSize.Height)
        try { $stage.DrawToBitmap($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height))) }
        finally { $bitmap.Dispose() }
    }
    $formType.GetField('_entries', $flags).GetValue($form).Clear()
    $formType.GetMethod('RefreshPinballText', $flags, $null, [type[]]@([bool]), $null).Invoke($form, @($false)) | Out-Null
    $formType.GetMethod('RefreshCounts', $flags).Invoke($form, @()) | Out-Null

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

    # Check every source file of the project, not only Program.cs.
    $sourceDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $SourcePath)
    $source = (Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) }) -join "`n"
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
