# Compressr

**Video in. DVD out.** A personal Windows desktop app for turning local videos
and permitted YouTube videos into one continuous autoplay DVD-Video, without a separate
HandBrake step or codec settings. This is source for personal use, not a packaged
or signed public release.

## Current verification status

The desktop app builds with **zero warnings/errors**. Generated-media checks
cover landscape, silent rotated portrait, PAL/NTSC, manual rotation and overlong
input and half-D1 encoding, plus ordered stitching with mixed formats and audio.
Eight authored ISOs have been mounted and their VIDEO_TS hashes matched.
Saved ISOs also reopen and verify independently of original source/job folders.
The focused/integration run passed 133 assertions, including image rejection,
capacity boundaries, image locking and read-back corruption.
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

1. Drop one or more files, browse (multi-select supported), or paste an HTTPS
   YouTube link and click **Add**. Files and links can be mixed. A single
   pending file/link is also added automatically on Preview, Check fit or Prepare DVD,
   preserving the quick single-video workflow. Only download/copy
   content you own or have permission to use; no DRM or login bypass is provided.
2. Use **Move up**, **Move down** and **Remove** to arrange parts. They play
   in the displayed order, joined into one title with no menu or transition.
   Select a part, then use **Preview / refresh** and **Rotate right** if needed.
   Each part keeps its own manual rotation. The preview uses the
   same padding/rotation geometry as the encode. Metadata rotation is automatic;
   the manual override adds quarter turns. YouTube preview downloads the source
   once and reuses it for that input.
3. Use **Check fit** for combined duration and single-/dual-layer bitrate estimates.
   For YouTube it requests public metadata only, without downloading the videos.
   A metadata failure makes fit unknown rather than assuming a duration.
   Prepare DVD checks fit again before downloads, then reprobes the downloaded
   originals before any conversion. Overlong parts are never silently dropped.
4. Choose PAL (default, remembered) or NTSC and single-layer or dual-layer capacity.
   Leave **Prepare as** on **DVD-ready ISO (burn later)**, click
   **Prepare DVD / save ISO**, and choose where to save it. No drive or inserted
   disc is required. The app converts, authors, builds and validates the image,
   and verifies its DVD files against the authored output before saving it.
   DVD folder output is still available, but the burn-later action takes an ISO.
5. Later, start the app with `.\scripts\Run.ps1`, insert a compatible blank DVD,
   select the writer (use **Refresh drives** after connecting one), and click
   **Burn prepared DVD…** to open the saved ISO. Confirm the named image and drive.
   No input videos need to be added, and PAL/capacity controls do not alter a
   prepared image. There is no download, conversion or authoring during this action.
   The single ISO is self-contained: original videos, downloads and job folders
   are not needed, even after restarting the app or moving the ISO elsewhere.
   Burning requires an explicit confirmation; Compressr never erases media.

YouTube downloads are capped at 1080p where that rendition exists: ample source
resolution for a DVD without an unnecessary 4K download.
YouTube `t=`, `start=` or `#t=` playback timestamps are incidental: **the whole
video is used**, not just the portion after that timestamp. There is no trimming
feature. Every listed source is included in full.
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
  stretching. A single source keeps automatic 4:3/16:9 selection; multi-video
  titles use a common 16:9 canvas with bars for narrower parts.
- Before joining, each source is normalized to common DVD geometry, pixel
  aspect, PAL/NTSC frame rate, zero-based timestamps and 48 kHz stereo audio.
  Silent parts get silence, short audio is padded, and mixed sample rates or
  channel layouts are handled. Concatenation happens inside the two-pass
  encoder; no giant intermediate uncompressed file or lossy pre-compression is needed.
- Two-pass bitrate budgeting uses **combined** source duration, 4.7 GB / 8.55 GB capacity,
  a 5% safety reserve, authoring reserve and mux overhead, capped at 8 Mbps video.
  Below 3 Mbps it uses half-D1 (352 pixels wide). Below 2 Mbps the operation
  stops **before encoding**, suggesting dual-layer or splitting. This is a
  conservative quality floor, not a guarantee every source will look good.
- Actual authored and image sizes are checked too; real blank-media capacity is
  checked before writing and again immediately before IMAPI writes. Not every burner supports
  every DVD type; dual-layer requires compatible media and hardware.
- `dvdauthor` produces uppercase VIDEO_TS navigation and a first-play command
  that starts the single title without a menu. `mkisofs -dvd-video -udf` supplies
  DVD file ordering/padding and a DVD-Video UDF 1.02 / ISO9660 image, rather than
  a generic data disc containing an MPEG file. Windows IMAPI2 writes that image
  with `ForceMediaToBeClosed=true`.
- Opening a prepared image checks complete sector/volume sizes, UDF 1.02
  descriptors and checksums, ISO file extents, uppercase DVD navigation, matching
  IFO backups, MPEG-2 PAL/NTSC title attributes and every VOB sector's pack header.
  It supports Compressr's single-title/no-menu layout, not arbitrary data ISOs
  or all third-party authored DVDs. Invalid/truncated/oversized/unsupported images
  fail before writing. Inspection reads all DVD files and can be cancelled.
  The ISO is locked against writes/replacement through inspection and burning.
  Structural validation is not a full decode of every frame and cannot detect
  every possible damaged source or prove player compatibility.
- Burn verification compares all VIDEO_TS file sizes and SHA-256 hashes from the
  saved ISO with the mounted disc, not with retained job files.
  It is file-level read-back, not an exhaustive sector scan or
  proof of playback on a particular set-top player. Verification failure is
  shown explicitly even after a burn has completed.
- One continuous title only; no chapter/menu editor, subtitles, DRM, source
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
rotation, padding, half-D1, ordered/combined fit and URL validation. Integration generates landscape
with audio, a silent phone-style file with rotation metadata, PAL and NTSC
outputs, a manual rotation case and half-D1 encoding. It authors DVD folders and ISOs, probes
decoded VOB codecs/geometry/DAR/frame rate/audio, checks actual decoded portrait side bars,
and rejects a compact genuine five-hour clip before conversion.
Additional stitched PAL, NTSC and reversed-order tests use distinct red/blue/green
clips with different lengths, 24/30/60 fps, non-square pixels, phone rotation,
44.1/32 kHz mono audio and a silent middle part. They check decoded clip order,
full combined duration, normalized output, portrait bars and silent audio after
real conversion and authoring.
Saved-image tests reopen all eight images while original videos and job folders
are unavailable, comparing against independent read-back copies. They reject
damaged ISO/UDF/navigation/program-stream headers, mismatched backups, truncation
and unsupported video attributes; test exact media boundaries, cancellation,
image write-locking and corrupted read-back files. None call IMAPI's write operation.
The PowerShell harness mounts each generated ISO and checks navigation files and
all VIDEO_TS hashes against the authored tree. It never writes or erases a
physical DVD. Results and ffprobe JSON are kept in ignored `artifacts`.

**Save screenshot** captures the running window to
`%LOCALAPPDATA%\Compressr\screenshot.png` for personal review.

Physical DVD writing, finalised disc read-back, older player compatibility,
and dual-layer layer-break behaviour require real hardware and remain untested.

## Four-part concert fit example

Public YouTube metadata checked on 1 November 2026 (no concert video downloaded):

| Part | YouTube ID | Reported duration |
|------|------------|-------------------|
| 1 | Ph7SZcdIK0E | 37:11 |
| 2 | 7OU91NR559A | 41:57 |
| 3 | f-NGGoCCQBA | 57:16 |
| 4 | LjAJzcHZSq0 | 38:08 |

The titles identify Bruce Springsteen / *Land of Hope and Dreams American Tour*
(Philadelphia Freedom Cut parts 2–4; part 1's published title is truncated).
Total: **2:54:32** (10,472 seconds), including all of part 4 despite its `t=1082s` link.
With the current overhead reserves and 192 kbps audio, it fits single-layer at
**3.063 Mbps** video, or dual-layer at **5.748 Mbps**, both full-D1.
Dual-layer provides appreciably more quality headroom for concert motion/detail.
**This exact concert remains untested end-to-end.** These are metadata-based fit estimates, not an assessment of this concert's
visual quality or proof of a burned disc. Availability/metadata may change.
Only generated clips and permitted media are used for the actual pipeline tests.
