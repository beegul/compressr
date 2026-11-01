# Compressr

**Video in. DVD out.** A personal Windows desktop app for turning one local video
or a permitted YouTube video into an autoplay DVD-Video, without a separate
HandBrake step or codec settings. This is source for personal use, not a packaged
or signed public release.

## Current verification status

The desktop app builds with **zero warnings/errors**. Generated-media checks
cover landscape, silent rotated portrait, PAL/NTSC, manual rotation and overlong
input and half-D1 encoding; five authored ISOs have been mounted and their
VIDEO_TS hashes matched. The final focused/integration run passed 57 assertions.
The YouTube path has downloaded and probed Blender's Creative Commons
*Big Buck Bunny* test video. Physical burning has **not** been performed.

## Setup and launch

Requires Windows 10/11, the **.NET 8 SDK**, internet access for dependency setup,
and several GB of free space (allow about three times the disc capacity plus the
original/downloaded source). No installer or signing is required.

In PowerShell at the repository root:

```powershell
.\scripts\Setup-Tools.ps1
.\scripts\Run.ps1
```

Setup downloads FFmpeg/ffprobe from Gyan's FFmpeg builds, yt-dlp and the Deno
JavaScript runtime from their official GitHub releases. Deno handles current
YouTube extraction challenges. It extracts the DVDStyler 3.2.1 PortableApps package
locally under `tools`, using its bundled native `dvdauthor` and `mkisofs`
executables with their DLLs. The portable package's SHA-256 is pinned to the
hash published by PortableApps and checked **before** extraction. Setup extracts
official 7-Zip MSI files locally to unpack the archive without running the
DVDStyler installer. This avoids
installing a system-wide authoring app or using older adware-bundled installers.
Setup errors are surfaced rather than silently skipped. No third-party binaries
are committed. Review the script and upstream downloads before running them.

Alternatively supply `ffmpeg.exe`, `ffprobe.exe`, `yt-dlp.exe`, `dvdauthor.exe`
and `mkisofs.exe` with their runtime libraries on PATH or in `tools`. Set
`COMPRESSR_TOOLS` to an alternative tools directory if desired. Do not copy just
authoring executables without their DLLs. Update the downloader when YouTube changes:

```powershell
.\scripts\Setup-Tools.ps1 -UpdateYouTube
```

## Use

1. Drop one video, browse, or paste an HTTPS YouTube link. Only download/copy
   content you own or have permission to use; no DRM or login bypass is provided.
2. Use **Preview / refresh** and **Rotate right** if needed. The preview uses the
   same padding/rotation geometry as the encode. Metadata rotation is automatic;
   the manual override adds quarter turns. YouTube preview downloads the source
   once and reuses it for that input.
3. Choose PAL (default, remembered) or NTSC, single-layer or dual-layer capacity,
   and ISO, DVD folder, or a detected writer. **Make DVD** runs the pipeline.
   Burning requires a blank writable DVD and an explicit confirmation. Compressr
   never erases media. ISO is the safe default for trying it without a writer.

YouTube downloads are capped at 1080p where that rendition exists: ample source
resolution for a DVD without an unnecessary 4K download.
The stages report download, two-pass conversion with percentage, authoring,
image creation, burn/finalisation and file read-back verification. Download,
authoring, imaging and burning currently show indeterminate progress rather
than invented percentages. Cancellation kills child tool processes before
burning; writing/finalising and read-back must finish before the app closes.

## DVD behaviour and limits

- MPEG-2 PAL 720×576 at 25 fps or NTSC 720×480 at 29.97 fps, AC-3 stereo
  192 kbps / 48 kHz; silence is supplied for sources without audio.
- Source sample aspect and rotation metadata are honoured. Display aspect is
  preserved using black letterbox/pillarbox bars, never intentional cropping or
  stretching. 4:3 or 16:9 DVD flags are selected automatically.
- Two-pass bitrate budgeting uses source duration, 4.7 GB / 8.55 GB capacity,
  a 5% safety reserve, authoring reserve and mux overhead, capped at 8 Mbps video.
  Below 3 Mbps it uses half-D1 (352 pixels wide). Below 2 Mbps the operation
  stops **before encoding**, suggesting dual-layer or splitting. This is a
  conservative quality floor, not a guarantee every source will look good.
- Actual authored and image sizes are checked too; real blank-media capacity is
  checked before conversion and again before writing. Not every burner supports
  every DVD type; dual-layer requires compatible media and hardware.
- `dvdauthor` produces uppercase VIDEO_TS navigation and a first-play command
  that starts the single title without a menu. `mkisofs -dvd-video -udf` supplies
  DVD file ordering/padding and a DVD-Video UDF 1.02 / ISO9660 image, rather than
  a generic data disc containing an MPEG file. Windows IMAPI2 writes that image
  with `ForceMediaToBeClosed=true`.
- Burn verification compares all VIDEO_TS file sizes and SHA-256 hashes with the
  mounted disc. It is file-level read-back, not an exhaustive sector scan or
  proof of playback on a particular set-top player. Verification failure is
  shown explicitly even after a burn has completed.
- One title only; no chapter/menu editor, subtitles, DRM, batch input, source
  splitting, or advanced quality controls. HDR/10-bit tone mapping and damaged
  or unusual streams are not specifically supported. FFmpeg/yt-dlp failures
  display tool diagnostics.
- Downloads and per-job work are retained under `%LOCALAPPDATA%\Compressr`
  for troubleshooting; remove specific completed job/download folders manually
  when no operation is running. Temporary preview frames are removed on close.

## Development and verification

```powershell
dotnet build .\Compressr.App\Compressr.App.csproj -c Release
.\scripts\Test.ps1
.\scripts\Test.ps1 -Integration
# Optional: supply a video you have permission to download.
.\scripts\Test.ps1 -Integration -YouTubeUrl 'https://www.youtube.com/watch?v=YOUR_VIDEO'
```

The dependency-free console test harness checks exact fit boundaries for both
capacities, invalid inputs, bitrate/mux budget, sample aspect, metadata/manual
rotation, padding, half-D1 and URL validation. Integration generates landscape
with audio, a silent phone-style file with rotation metadata, PAL and NTSC
outputs, a manual rotation case and half-D1 encoding. It authors DVD folders and ISOs, probes
decoded VOB codecs/geometry/DAR/frame rate/audio, checks actual decoded portrait side bars,
and rejects a compact genuine five-hour clip before conversion.
The PowerShell harness mounts each generated ISO and checks navigation files and
all VIDEO_TS hashes against the authored tree. It never writes or erases a
physical DVD. Results and ffprobe JSON are kept in ignored `artifacts`.

**Save screenshot** captures the running window to
`%LOCALAPPDATA%\Compressr\screenshot.png` for personal review.

Physical DVD writing, finalised disc read-back, older player compatibility,
and dual-layer layer-break behaviour require real hardware and remain untested.
