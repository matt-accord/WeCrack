# WeCrack

**Unlock WeMod Pro for free.**
WeCrack is a small Windows app that turns on every WeMod Pro feature and
removes the 2-hour daily limit — without paying for a subscription.

![WeCrack interface](WeCrack/resources/app.png)

---

## What it does

- **No more 2-hour timer.** Free WeMod caps you at two hours of cheats a day. WeCrack removes that.
- **All Pro features unlocked.** Pro-only cheats, no upgrade popups, Pro badge on your profile.
- **Fully reversible.** Every file it touches is backed up first. One click puts everything back.

---

## How to use it

1. **Download** `WeCrack.exe` from the [Releases](../../releases) page.
2. **Close WeMod** if it's running.
3. **Double-click** `WeCrack.exe`.
4. Click the green **`[ 1 ] INSTALL`** button.
5. Open WeMod. You're Pro.

That's it.

### To undo everything

Open `WeCrack.exe` again and click **`[ 2 ] UNINSTALL`**. Your original WeMod files come back exactly as they were before.

---

## The three buttons

| Button | What it does |
| --- | --- |
| **`[ 1 ] INSTALL`** | Patches WeMod so you get Pro. Saves a backup of every file first. |
| **`[ 2 ] UNINSTALL`** | Restores the backup. Undoes everything. |
| **`[ 3 ] VERIFY`** | Read-only check. Tells you the current state of your install. |

## What the status colors mean

| Status | Colour | Meaning | What to do |
| --- | --- | --- | --- |
| **CLEAN** | cyan | Never patched, nothing changed | Click INSTALL |
| **READY** | cyan | Backups exist, patch hasn't been applied yet | Click INSTALL |
| **PATCHED** | green | Pro is unlocked and working | Nothing — enjoy |
| **MIXED** | red | Files look inconsistent | Look at the file list in the app |

---

## FAQ

**Is it safe?**
Yes. WeCrack only edits WeMod's own files, saves a backup of every one before touching it, and can undo everything with one click.

**Does WeMod have to be closed?**
Yes. The tool refuses to run while WeMod is open, because Windows won't let anything modify a file that's currently in use.

**What happens when WeMod updates itself?**
The patch stops working on the new version. Just open WeCrack again and click INSTALL — it finds the new files automatically and re-patches.

**Do I need administrator rights?**
No. WeCrack only touches files in your own user folder (`%LocalAppData%\Wand`).

**Will WeMod's servers know?**
No. The change is entirely on your computer. Nothing gets sent anywhere. You still sign in with your normal free account.

**Why is my antivirus flagging it?**
Some antivirus programs flag any small unsigned tool that modifies another program. WeCrack is open source — every line of code is in this repository. If your antivirus blocks it, either add an exception or [build it yourself](#build-from-source).

**Does it work on WeMod version X.Y.Z?**
Almost certainly. The patch finds its targets by shape, not by exact filename, so it survives WeMod's frequent auto-updates. If a new version ever breaks it, open an issue.

**What if WeCrack can't find WeMod on my PC?**
It will show **NOT FOUND** in the STATUS card and a `[ Browse for WeMod folder... ]` button. Click it, point at your WeMod install (the folder that contains `app-XX.YY.Z` subfolders — usually `C:\Users\<you>\AppData\Local\Wand`), and WeCrack remembers it for next time. The tool also checks the Windows registry automatically, so most non-standard install locations still work without any manual step.

---

## How it works (short version)

WeMod is an Electron app — a Chromium browser packaged with a big JavaScript file called `app.asar`. WeCrack:

1. Opens `app.asar` and finds the JavaScript that decides "is this user Pro?" — rewrites it to always say **yes**.
2. Finds the 2-hour timer code — rewrites it to be **permanently disabled**.
3. Wraps the API call that fetches your account info to **inject a fake Pro subscription** into the response.
4. Flips **one byte** inside `Wand.exe` to disable the integrity check that would otherwise notice the changes above.

Every step is reversible. The original files are saved as `.orig` next to them.

---

## Build from source

You need [Visual Studio 2026](https://visualstudio.microsoft.com/) or the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/luckyman-01/WeCrack.git
cd WeCrack
dotnet publish -c Release
```

The finished `WeCrack.exe` lands in the `Build\` folder.

---

## Project layout

```
WeCrack\
├── WeCrack.sln              solution file (open this in VS 2026)
├── README.md                you are here
├── Build\
│   └── WeCrack.exe          the shippable — single file, 62 MB, no dependencies
└── WeCrack\                 source
    ├── App.xaml             theme (colors, fonts, button and scrollbar styles)
    ├── MainWindow.xaml      the UI you see
    ├── MainWindow.xaml.cs   button click handlers, log rendering, window chrome
    ├── CrackService.cs      the install / uninstall / verify workflow
    ├── AsarArchive.cs       reads and writes Electron .asar files
    ├── Patches.cs           the JavaScript patches (regex-based, survives WeMod updates)
    ├── WandInstall.cs       finds your WeMod install (config, registry, default), patches the fuse byte
    ├── Config.cs            saves your browsed install path to %LocalAppData%\WeCrack\config.json
    ├── app.manifest         DPI-awareness for sharp text on 4K displays
    └── resources\
        ├── icon.ico         app icon (taskbar / alt-tab / Explorer)
        └── app.png          the screenshot at the top of this README
```

---

## License

Do what you want with this. It's provided as-is, with no warranty.
