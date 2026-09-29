$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('vmnotify-notes-test-' + [guid]::NewGuid().ToString('N'))
New-Item (Join-Path $root 'windows') -ItemType Directory -Force | Out-Null
try {
    Set-Content (Join-Path $root 'windows/VERSION') '1.2.3'
    $heading = '## Windows 1.2.3 / Linux 0.2.0'
    $valid = "$heading`n`n- 中文更新说明`n`n## Windows 1.2.2 / Linux 0.2.0`n`n- OLD-ENTRY"
    $output = Join-Path $root 'notes.md'
    Set-Content (Join-Path $root 'CHANGELOG.md') $valid
    & "$PSScriptRoot/release-notes.ps1" -RepositoryRoot $root -OutputPath $output
    $notes = Get-Content $output -Raw
    if (!$notes.StartsWith('# VMNotify v1.2.3') -or !$notes.Contains('中文更新说明') -or $notes.Contains('OLD-ENTRY')) {
        throw 'Wrong changelog section selected'
    }
    foreach ($invalid in @('## Windows 9.9.9 / Linux 0.2.0', "$heading`n", "$heading`n- One`n$heading`n- Two")) {
        Set-Content (Join-Path $root 'CHANGELOG.md') $invalid
        $rejected = $false
        try { & "$PSScriptRoot/release-notes.ps1" -RepositoryRoot $root -OutputPath $output } catch { $rejected = $true }
        if (!$rejected) { throw 'Missing, empty or duplicate release notes accepted' }
    }
    Write-Host 'PASS: version-specific notes, Unicode, section boundary and missing/empty/duplicate rejection'
} finally {
    # Delete only the files created above; no recursive directory deletion.
    foreach ($file in @('windows/VERSION', 'CHANGELOG.md', 'notes.md')) {
        $path = Join-Path $root $file
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    [IO.Directory]::Delete((Join-Path $root 'windows'))
    [IO.Directory]::Delete($root)
}
