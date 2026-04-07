$root    = Split-Path $PSScriptRoot -Parent
$adb     = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$apk     = "$root\CalendarParse\bin\Debug\net10.0-android\com.companyname.calendarparse-Signed.apk"
$package = "com.companyname.calendarparse"
$activity = "crc64a73038c317658f4e.MainActivity"

# 1. Build
dotnet build "$root\CalendarParse" -f net10.0-android -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 2. Install
Write-Host "Installing APK..."
& $adb install -r $apk
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. Launch
Write-Host "Launching app..."
& $adb shell am start -n "$package/$activity"
