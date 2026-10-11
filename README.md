# ProPresenter Assistant

A Windows tray app for importing images and videos from USB drives, local files, folders, ZIP, 7z, RAR and TAR archives. One selection becomes one named presentation in an existing ProPresenter playlist.

## Run

Run **ProPresenterAssistant-Setup.exe** to install for your Windows account. It creates a Start menu shortcut and **Import into ProPresenter** in the first Windows 11 right-click menu for files and folders; desktop and sign-in startup are optional. Upgrading removes the old Send To shortcut. First installation requests Windows administrator approval to trust the locally signed shell package certificate in the machine Trusted People store. The app itself runs as your normal Windows user. No separate .NET runtime or FFmpeg installation is needed. Alternatively, run the portable **ProPresenterAssistant.exe**.

1. Enable Network access in ProPresenter. The assistant reads ProPresenter's Network preferences, detects its port, and verifies the local API automatically. Settings shows whether the API is disabled, enabled and responding, enabled but unreachable, or unknown. Automatic detection defaults on; turn it off for a manual port override (fallback 50001). ProPresenter's settings are read only, never changed by the assistant. Available playlists load automatically and refresh whenever you expand the dropdown, preserving your selection. Background checks detect when ProPresenter starts or stops; the dropdown is disabled while ProPresenter or its API is unavailable. Choosing media starts ProPresenter if needed.
2. Insert a USB drive, choose files/folder, drag files into the app, or right-click selected files/folders in Explorer and choose **Import into ProPresenter**. Right-click a folder to include its media recursively, initially all selected. Right-click a picture to browse all pictures in its containing folder and subfolders, initially selecting only the clicked picture. For multiple selected pictures, only those pictures start selected. Other nearby videos and archives are included only when explicitly selected or when importing their folder. Multi-selection enters one import workflow. If Explorer has cached its menu, sign out and back in to reload the extension.
3. Choose a playlist and select thumbnails. Select all applies to the current filter/search, across all pages.
4. Review, name the presentation, and import. If ProPresenter asks for a library, choose it there.
5. Wait for verified completion. If media remains, choose whether to create another item.

Closing the window keeps the tray app running. Use **Exit** in the tray menu to stop monitoring. Existing USB drives at startup are not automatically scanned; choose their folder instead.

Disk and folder scans skip files and folders marked Hidden in Windows, including all contents of hidden folders. Hidden archives are also skipped. This applies to USB scans, chosen folders and Explorer imports.

## Media and recovery

Media is copied to the Windows Documents known folder under **ProPresenter Imported Media**, with a unique folder per import. Still images are normalized to RGB/RGBA PNG. Compatible MOV/MP4 videos are retained; unsupported codecs/containers convert to H.264/AAC MP4. Alpha video converts to ProRes 4444 MOV when needed. FFmpeg/FFprobe are embedded and extracted to a per-user cache on first use. Conversion runs below normal priority with limited threads.

ZIP/7z/RAR/TAR entries are indexed; only media is extracted. Encrypted entries, path traversal, reparse points and oversized archives are rejected/skipped. Limits: 10,000 archive entries, 8 GiB per entry, 32 GiB total extraction, folder depth 32. Nested archives are not recursively unpacked. Some rare archive/media codecs may be unsupported and are reported.

Prepared imports and errors are recorded under `%LOCALAPPDATA%\ProPresenterAssistant`. **Resume import** reuses copied media and prevents duplicate playlist references. If preparation itself fails, select the files again; a partial folder is retained for inspection. The playlist before each append is saved in the import folder as `playlist-before.json`.

## ProPresenter integration and current limits

The public HTTP API lists playlists and updates their contents but does not document creating a media presentation. The assistant writes a minimal native `.pro` protobuf presentation into an existing library, which ProPresenter indexes. The Default library is discovered from API-reported presentation paths; an optional library-folder setting overrides discovery. If every library is empty and no folder is configured, the app falls back to ProPresenter's file-open handler. The field layout was verified against the installed **21.4.2** serialization reader and running app. No vendor DLLs are included. This is an undocumented file format and must be validated with each supported ProPresenter version.

The app confirms the expected slide count via the API and checks the playlist reference before reporting completion. Playlist updates normalize the required target_uuid for every existing item, including headers. ProPresenter regenerates header item IDs during updates; verification compares content and order, including colors, timing and arrangements. Live testing on ProPresenter 21.4.2, port 53532, confirmed automatic library discovery, native import of two images and one video, three indexed slides, an image thumbnail, successful linking to a dedicated test playlist, and completion without duplicate entries. Offline tests cover archive extraction, video conversion, cancellation and playlist preservation/conflict handling. Playback through output screens, physical USB insertion, The native Explorer command was tested for COM activation, its exact label, top-level flags, state, multi-selection handoff and Unicode paths. Appearance in the live Windows 11 menu and a full installer/uninstaller cycle remain manual acceptance checks. The final live preservation probe was blocked by automatic approval review. If importing does not start, use **File → Import → File** in ProPresenter to import the prepared `.pro`, then Resume import.

Playlist writes use read/check/replace/verify with a backup. The public API exposes no atomic compare-and-swap: an operator edit in the tiny interval between the final read and write cannot be excluded. Avoid editing the target playlist during import. No slides are triggered and no output screens are changed.

App and installer EXEs are unsigned. The Windows 11 shell identity package is signed using a local development certificate; the private signing key is never bundled. Broad distribution should use a publicly trusted production signing certificate. Uninstall unregisters the context-menu package; the signing certificate remains in the machine Trusted People store so other Windows accounts using the same build remain supported. Uninstall runs from a temporary copy so the app directory can be removed; media and user records remain intact.

## Build and test

Requires a .NET 9 SDK, Visual Studio C++ x64 build tools and the Windows SDK on Windows x64. Windows 11 is required for installer context-menu registration. `./build.ps1` downloads pinned FFmpeg tools and produces the self-contained EXE, portable ZIP and installer in `dist`. For a locally installed SDK, pass `-Dotnet <absolute path to dotnet.exe>`.

Run `ProPresenterAssistant.exe --self-test <absolute report.json>` for extraction, conversion and HTTP integration tests. Run `--demo --screenshot <absolute preview.png>` to render a selection-screen preview without connecting to ProPresenter. Run `./test-shell.ps1` for native COM and multi-selection handoff tests using a capture stub (without touching ProPresenter). Installer `--verify-payload <absolute report.txt>` verifies its embedded package without installing.

Acceptance test: import two images and one video to a test playlist, verify alphabetical order and playback, remove the source USB drive and replay all three, then test a partial selection followed by another item. Verify USB insertion, multi-file Import into ProPresenter, cancellation/resume, multiple libraries and setup/uninstall on a clean Windows account.

References: [public API](https://openapi.propresenter.com/), [media formats](https://support.renewedvision.com/hc/en-us/articles/360058902173-What-file-types-work-with-ProPresenter), [native file import](https://learn.renewedvision.com/propresenter/working-with-files).
