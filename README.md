# Compressr

**Video in. DVD out.** A personal Windows desktop app for turning local videos
and permitted YouTube videos into one continuous autoplay DVD-Video, without a separate
HandBrake step or codec settings. Includes a local personal portable packaging
command, not an installer or signed public release.

The main window follows **your videos → your DVD → Prepare DVD**. Browse or
drop one or several files, or add a permitted YouTube link; the queue is the
playback order. Orientation preview and rotation stay beside the queue. The DVD
panel contains PAL/NTSC, 4.7/8.5 GB capacity, ISO/folder output and Check fit.
**Burn prepared DVDs** is a separate tab for saved ISOs, the writer and Check disc.
The output starts on **Prepare and burn DVD** only when a read-only startup check
finds a compatible blank DVD large enough for the selected capacity; otherwise it
starts on **Save DVD-ready ISO**. Refresh/check-disc can update this automatic
suggestion, but never changes a manual output choice. **Use automatic** restores
automatic suggestions. Media removal, nonblank/protected/finalized/damaged media,
unsupported types, insufficient capacity and detection failures are shown explicitly.
Detection is a snapshot, not a reservation: the exact image and current media are
checked again before confirmation and immediately before writing.

The **Save to** path is visible before preparing. **Change…** opens the native
ISO save dialog (or a parent-folder picker for DVD folders); cancelling keeps the
previous path. Existing ISO replacement requires the native overwrite confirmation.
Preparation does not silently switch to saving after a declined or failed burn.

Each successful ISO preparation is added to **Prepared DVDs**. Add multiple
existing ISOs with **Add prepared ISOs…**, then select **one** and burn it to **one**
blank DVD. Repeat with another file and a new blank disc. The list survives app
restarts; files are validated when added and again before burning. **Remove from
list** never deletes an ISO. Source videos above are concatenated into one DVD;
prepared ISOs are distinct disc jobs, never concatenated or written as multisession
data, and the list is not an automatic multi-disc/bulk conversion scheduler.
Preparation and status remain visible while the content scrolls on smaller screens.
The interface uses the native Segoe UI family, a restrained teal accent, readable
sentence-case labels and keyboard focus indicators, following
[Windows typography guidance](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
and [accessible text contrast guidance](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements).

## Current verification status

The desktop app builds with **zero warnings/errors**. Generated-media checks
cover landscape, silent rotated portrait, PAL/NTSC, manual rotation and overlong
input and half-D1 encoding, plus ordered stitching with mixed formats and audio.
Eight authored ISOs have been mounted and their VIDEO_TS hashes matched.
Saved ISOs also reopen and verify independently of original source/job folders.
The focused/integration run passed 168 assertions, including image rejection,
capacity boundaries, image locking and read-back corruption.
The YouTube path has downloaded and probed Blender's Creative Commons
*Big Buck Bunny* test video. One user-authorized physical DVD+RW burn on an
HL-DT-ST GT50N succeeded: finalisation returned successfully, all five VIDEO_TS
files matched the saved ISO, and actual on-disc PAL video/audio decoded correctly.
Standalone-player playback and full-disc/dual-layer burns remain untested.

## Setup and launch

### Pull, build, run

Install the **.NET 8 SDK for Windows x64** once (the runtime alone is not enough).
In Windows PowerShell or PowerShell 7:

```powershell
git clone https://github.com/beegul/compressr.git
cd compressr
.\Build.ps1
.\Compressr.exe
```

You can instead double-click **Compressr.exe directly in the repo folder**.
If PowerShell blocks local scripts, use
`powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1` for this invocation.
Build automatically downloads missing tools using the existing setup script,
publishes with the .NET 8 SDK and places the complete self-contained app beside
the source. No separate manual setup command is needed.

For later updates, close Compressr first:

```powershell
git pull --ff-only
.\Build.ps1
.\Compressr.exe
```

Repeat builds reuse downloaded tools and cached notices. Previously managed
runtime files are safely replaced; unrecognized or locally edited destination
files and locked files cause actionable errors before replacement. Do not edit
the ignored `.compressr-portable.json` ownership record. The repository README
and source are never replaced by the portable bundle.

Windows 10/11 **x64** only. Allow approximately **1 GB for the runnable folder**
and several GB for downloads, build output and retained staging/rollback folders
under ignored `artifacts`. Exact sizes vary with upstream tool versions.
First build requires internet for tools, SDK restore and license notices;
cached builds normally need no tool downloads. Launch needs no SDK, installer,
PATH changes or worktree. Keep runtime DLLs, language folders, `licenses` and
`tools` alongside the executable. DVD jobs need additional space as described below.

This personal build is English-only: .NET publishes neutral/English resources,
not non-English runtime satellite folders. Rebuilding an older bundle removes
only unchanged, previously managed non-English satellite files and their empty
folders. Unknown or edited files are preserved (edited managed files stop the
build with an explicit error). Third-party tool resources and licenses remain intact.

**Already have the personal portable ZIP?** Extract the entire ZIP, then
double-click **Compressr.exe** inside the Compressr folder. Keep its `tools`,
runtime DLLs and license notices together. No .NET installation, SDK, command
or dependency download is needed to run it. Local conversion works offline;
YouTube still requires internet. Windows 10/11 **x64** is the supported target.
Use a writable output location and allow several GB of temporary disk space.

**Executable beside the repository source:** double-click `Compressr.exe` at
the repository root once the portable bundle has been placed there. The adjacent
DLLs, language folders, `licenses` and `tools` are required; keep them together.
These local runtime files are ignored by Git, not uploaded with the source.
To place an existing complete bundle there without an SDK or downloads:

```powershell
.\scripts\Place-Portable.ps1 -BundleDirectory 'C:\path to portable bundle\Compressr'
```

Placement checks bundle hashes and all destination collisions before copying,
preserves the repository README, and adopts identical existing files.
Only files matching a previous managed hash may be replaced on later builds.

To reproduce the bundle from source, first complete dependency setup below, then:

```powershell
.\scripts\Package-Portable.ps1
```

This publishes an untrimmed self-contained win-x64 app and copies the processing
tools, authoring DLLs and upstream notices into a fresh ignored
`artifacts\portable-<timestamp>\Compressr` folder plus ZIP.
Use `-OutputDirectory 'C:\path with spaces\Compressr'` for a new destination or
`-SkipZip` for just the folder. Packaging requires the SDK and internet for
uncached license notices; launching the result requires neither.
Add `-RepositoryRoot` to also place the finished bundle beside the source, ready
to launch as `.\Compressr.exe`. Use `.\Build.ps1` for automatic missing-tool setup.
Tool discovery starts beside the executable, independent of working directory.
`COMPRESSR_TOOLS`, if explicitly set, still overrides the bundle.
Settings, previews, downloads and job folders remain per-user in
`%LOCALAPPDATA%\Compressr`; moving the bundle does not move that working state.
Third-party binaries are never committed. This is a local personal bundle,
not a public redistribution or signing/release workflow.

### Running from source

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
   Choose **Save DVD-ready ISO** and check the visible **Save to** path (use
   **Change…** to choose a filename), then click **Prepare DVD**. No drive or inserted
   disc is required. The app converts, authors, builds and validates the image,
   and verifies its DVD files against the authored output before saving it.
   DVD folder output is still available, but the burn-later action takes an ISO.
   If a suitable blank disc is detected, automatic output instead selects
   **Prepare and burn DVD**; preparation stages a validated ISO and asks for
   confirmation before writing. A declined/failed burn keeps that staged ISO
   in the prepared list with its location/status, not as a silent file-output fallback.
5. Later, double-click portable **Compressr.exe** (or use `.\scripts\Run.ps1`
   from source), insert a compatible blank DVD,
   select the writer (use **Refresh drives** after connecting one). **Check disc**
   reports the current format, blank state and exact available bytes without
   writing, erasing or ejecting it. Then click
   **Burn prepared DVDs**, add the saved ISO(s), select one and click
   **Burn selected DVD…**. Confirm the named image and drive.
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
- For UK players, prepare **PAL** DVD-Video. Autoplay navigation starts title 1
  and generated DVD navigation has no region restrictions. Recordable-format
  support is separate: older players may accept DVD-R/+R but not DVD-RW/+RW.
  DVD+R book-type/bitsetting is drive-dependent and is not changed by Compressr.
  DVD-RAM is rejected for ordinary-player output; it is not widely supported
  by set-top players. Dual-layer needs a compatible writer and player; layer-break
  positioning is left to IMAPI and has not been hardware-tested.
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
# Native UI checks, using PowerShell 7 in STA mode after the Release build.
pwsh -STA -File .\scripts\Test-Interface.ps1
.\scripts\Test-Placement.ps1
.\scripts\Test.ps1 -Integration
# Optional: supply a video you have permission to download.
.\scripts\Test.ps1 -Integration -YouTubeUrl 'https://www.youtube.com/watch?v=YOUR_VIDEO'
```

Placement checks cover first placement, repeat builds, managed replacement,
source README preservation, unexpected collisions and locked-file rejection
using disposable fixtures, without downloads or disc operations.

Native interface checks cover retained controls and handlers, empty/single/multiple
queues, reordering/removal, busy/idle state, output choices, burn-later access,
compact-window layout and text-token contrast. An optional `-ScreenshotDirectory`
captures offscreen WPF renders; these complement, rather than replace, inspection
of the running window. Supply `-PreparedIsoPaths` with an array of generated ISO
paths to check validation, selection and persisted jobs. Supply `-SourceVideoPaths`
with generated video paths to exercise source-to-ISO and source-to-folder
preparation, list restoration and explicit failure without a selected writer.
These checks use isolated temporary app state. No URL is downloaded and no
physical disc is written.

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
Media checks cover supported recordable types, nonblank rewritable rejection,
invalid capacities, blocking IMAPI media states and the actual attached DVD+RW's
single-layer boundary. Typed automatic-output checks cover ready, absent,
undersized and failed media detection, disc removal/replacement, capacity changes
and preservation of explicit output choices.
Authored IFO tests check a zero region-prohibition mask and the actual first-play
command bytes jumping to title 1, rather than relying only on the author XML.
The PowerShell harness mounts each generated ISO and checks navigation files and
all VIDEO_TS hashes against the authored tree. It never writes or erases a
physical DVD. Results and ffprobe JSON are kept in ignored `artifacts`.

**Save screenshot** captures the running window to
`%LOCALAPPDATA%\Compressr\screenshot.png` for personal review.

Short PAL DVD+RW writing and file read-back have been tested on the attached
GT50N. Older player compatibility, other physical media, near-capacity burns
and dual-layer layer-break behaviour remain untested.

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

## Attached disc check (1 November 2026)

Read-only IMAPI inspection detected **HL-DT-ST DVDRAM GT50N**, firmware **LC01**,
on `D:\`, with a **blank DVD+RW** (type 7). Both blank checks were true:
2,295,104 total/free sectors, **4,700,372,992 bytes** available. IMAPI supports
the recorder and inserted medium. The app's real no-write capacity check accepts
the generated PAL sample ISO and rejects a dual-layer-sized image on this disc.
Use **SingleLayer** when preparing for these discs: the 2:54:32 concert metadata
still budgets **3.063 Mbps** video. A dual-layer preparation at 5.748 Mbps would
not fit this single-layer disc.

The initial inspection was read-only. In a subsequent user-authorized test,
**one** generated ordered red/blue/green PAL ISO was burned through the app.
IMAPI writing/finalisation and the app's read-back verification succeeded;
all five actual VIDEO_TS files matched the saved image by size/SHA-256.
On-disc ffprobe confirmed MPEG-2, 720x576, 25 fps, 16:9, AC-3 stereo 48 kHz.
Strict sequential video/audio decode passed; decoded samples proved clip order,
rotated portrait bars and silent middle audio. Actual navigation is region-free
and jumps directly to title 1; readable physical descriptors show ISO9660/UDF 1.02.
No erase, retry or eject was performed; the disc is now nonblank with the short
test DVD on it. Windows raw reads exposed 457 sectors, all identical to the ISO,
but did not expose its remaining padding/backup-anchor tail; this is not a
full sector-for-sector verification of the complete ISO.
Playback on a UK set-top player, near-capacity/long burns and dual-layer writing
remain unverified. The concert was not downloaded or tested end-to-end.
