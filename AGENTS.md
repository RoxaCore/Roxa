# AGENTS.md

## Project Overview
XrayClient (branded as Roxa) is a desktop VPN/proxy client built with Avalonia UI 12.1.2 targeting .NET 8. The UI is in Persian (Farsi) with RTL layout.

## Build & Run
```powershell
# Kill existing instance first
Stop-Process -Name XrayClient -Force -ErrorAction SilentlyContinue

# Build
cd "D:\ai project freebuff\hiku\XrayClient"
& "C:\Program Files\dotnet\dotnet.exe" build -c Release

# Run (verify it doesn't crash)
$p = Start-Process -FilePath "D:\ai project freebuff\hiku\XrayClient\bin\Release\net8.0\XrayClient.exe" -PassThru
Start-Sleep -Seconds 3
if ($p.HasExited) { "CRASHED: exit code $($p.ExitCode)" } else { "RUNNING (PID $($p.Id))" }
```

## Key Architecture

### MVVM Pattern
- **Models/**: `ServerInfo.cs` (server data, uses `[ObservableProperty]` from CommunityToolkit.Mvvm)
- **ViewModels/**: `MainViewModel.cs` (main logic, commands, sorting), `ServerGroup.cs` (grouping)
- **Views/**: `MainWindow.axaml` + `.cs` (UI with DataGrid, context menus, dialogs)
- **Core/**: `RealDelayTester.cs`, `SpeedTester.cs`, `LatencyTester.cs` (network tests)
- **Converters/**: `BoolNegateConverter.cs` (only converter available)

### Important Properties on ServerInfo
- `LatencyDisplay`: `-1` → "—", `-2` → "ناموفق", else `"{ms} ms"`
- `SpeedDisplay`: `< -1.5` → "خطا", `< -0.5` → "—", else `"{mbps} Mb/s"`
- `IsActiveConnection`: `bool`, must be set via `RefreshConnectionFlags()` in ViewModel
- `ProtocolDisplay`, `Security`, `Network` (Transport), `SNI`, `Flag` (country emoji)

### Connection Flow
- `ConnectServer()` → sets `SelectedServer` → calls `RefreshConnectionFlags()` → calls `Connect()`
- `Disconnect()` → stops engine → sets `IsConnected = false` → calls `RefreshConnectionFlags()`
- **Always call `RefreshConnectionFlags()`** when connection state changes, otherwise UI won't update

### Sorting
- ComboBox uses `ItemsSource="{Binding SortOptions}"` with Persian strings: "بدون مرتب", "تأخیر (کم)", "سرعت (بیش)", "نام"
- `SortField` property triggers `RefreshGroups()` via `OnSortFieldChanged`
- Sorting algorithm in `SortServers()` method

## Avalonia 12.1.2 Quirks
- `Grid` does NOT have Padding/BorderThickness — must wrap in `Border`
- `CaptureMouse`/`ReleaseMouseCapture` don't exist
- `Cursor="SizeWE"` is invalid — use `Cursor="Hand"`
- `Flex` property doesn't exist
- `xmlns:dg="using:Avalonia.Controls"` — DO NOT use `clr-namespace:...;assembly=...` format
- Package `Avalonia.Controls.DataGrid` Version 12.1.2 installed (match Avalonia version)

## Conventions
- UI language: Persian (Farsi), RTL layout (`FlowDirection="RightToLeft"`)
- Window name: `x:Name="RootWindow"` (don't change — causes conflicts with class name)
- All binding paths use `#RootWindow.DataContext` for commands in DataTemplates
- Default SortField: "بدون مرتب" (no sort)
- Server selection checkboxes use `IsChecked="{Binding IsChecked, Mode=TwoWay}"` on ServerInfo
- Confirm dialogs use custom XAML window with Click handlers (not MessageBox)

## Common Pitfalls
- Don't use `ListBox` for server list — use `ItemsControl` with `Expander` groups
- Don't change `RefreshConnectionFlags()` logic — it uses `ReferenceEquals` for exact match
- `SortServers()` modifies the list in-place (Clear + AddRange)
- All Avalonia packages MUST be same version (12.1.2)
