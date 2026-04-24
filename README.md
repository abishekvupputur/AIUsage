# CoPilot AI Usage

A lightweight Windows system tray app that shows your Copilot / Claude AI session and weekly usage at a glance.

## What it does

- Sits in the **system tray** with a usage indicator icon
- **Left-click** the icon to open a popup with two progress bars — current session and weekly usage
- Auto-refreshes on a configurable interval (1, 5, 15, or 60 minutes)
- Manual refresh button in the popup header

## Setup

1. Run `AIUsage.exe`
2. Right-click the tray icon → **Settings**
3. Paste your `sessionKey` cookie from claude.ai (F12 → Application → Cookies → claude.ai)
4. Click **Test** to verify, then **Save**

## Build from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
```

Standalone executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

## Demo mode

```powershell
AIUsage.exe --demo
```

Cycles through usage levels without needing a real session key.

---

Created by TobiasW & Abishek Narasimhan — forked from [TobiasW-T/CopilotUsage](https://github.com/TobiasW-T/CopilotUsage)
