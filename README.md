# Argus

Tray app for Windows 11 that silently re-checks, at random intervals and using only the infrared
Windows Hello camera, that you are the person in front of the laptop. Stranger or nobody there ->
short animation -> PC locks.

Everything is local: no network code at runtime, no images stored (only an encrypted face "fingerprint").

## Install

Download the latest from the **[Releases](../../releases)** page:

| File | What it is |
|---|---|
| `Argus-Setup-<version>.exe` | Installer (Start menu entry, uninstaller, no admin needed). Includes everything, no .NET required. |
| `Argus-<version>-win-x64-portable.zip` | Unzip anywhere and run `Argus.exe`. |

Requirements: Windows 11 x64 with a Windows Hello IR camera, and
Settings > Privacy & security > Camera > **"Let desktop apps access your camera" = ON**.

## First run
1. Launch Argus; it lives in the system tray.
2. Click the tray icon -> **Enroll / Re-enroll**. Look at the screen, then turn your head slightly (~30 s).
   Checks stay off until you enroll.

## Using it
- Tray icon: Argus eye with a status dot (green idle, amber checking, grey paused/locked, blue needs enrolling, red camera problem).
- Left click = settings. Right click = Check now, Pause (15 min / 1 h / until resume), Exit.
- Data: `%AppData%\Argus\` (`config.json`, `activity.log`, `face.dat` encrypted for your Windows user only).

## Tuning
- **Match strictness**: raise it if strangers get through, lower it if you get false "different face" locks (default 0.36).
- Re-enroll in different lighting/angles (with and without glasses) for fewer misses.
- A single bad scan never locks you: no-face scans are retried (3 times by default) and typing delays the lock.
  A clearly different face locks after 2 checks in a row.

## Troubleshooting
- "IR camera blocked / busy / not found": check the privacy toggle, close apps using the camera, confirm Windows Hello works.
  Argus never locks because of camera problems; it retries every 5 minutes.
- Locked right after unlocking? There is a 45 s grace window; a clearly different face still locks.
- Won't start: it's probably already running (check the tray).

## Build from source
Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
git clone <your-repo-url>
cd Argus
dotnet run --project src/Argus -c Release
```

The first build downloads the two face models (SHA-256 verified) into `src/Argus/models/` automatically
via `scripts/get-models.ps1`. They are intentionally not stored in git.

Self-contained publish (what releases use):

```powershell
dotnet publish src/Argus/Argus.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

To build the installer locally, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) and run
`iscc /DAppVersion=1.0.0 installer\Argus.iss` after publishing.

## Releasing
```powershell
git tag v1.0.1
git push origin v1.0.1
```
The **Release** workflow builds the installer and portable zip and attaches them (plus `SHA256SUMS.txt`) to a GitHub Release.

## Models
Face detection: [YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet);
recognition: [SFace](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface),
both from the OpenCV Zoo. Check their licenses before redistributing.
