# Muralis v0.2.0

Muralis v0.2.0 expands the project from a static wallpaper manager into a more capable Windows desktop personalization application, introducing the first version of the dynamic wallpaper engine alongside major reliability improvements.

## Highlights

### Dynamic Wallpaper Engine

- Play video wallpapers directly on the Windows desktop behind desktop icons
- Hardware-accelerated video playback
- Automatic video looping
- Restore the active dynamic wallpaper when Muralis starts
- Clean Start / Stop lifecycle without modifying the existing static wallpaper
- Automatic recovery after Windows Explorer restarts

### Improved Windows Shell Reliability

- Added single-instance protection
- Launching Muralis again now activates the existing instance instead of creating another process
- Tray icon automatically returns after Explorer restarts
- Dynamic wallpaper automatically reconnects to the rebuilt desktop host after Explorer restarts

### Wallpaper Experience

- Online wallpaper browsing
- Multiple wallpaper providers
- Search and filtering by orientation, resolution, category, and source
- Favorites
- Local wallpaper library
- Editable tags and tag filtering
- Automatic wallpaper rotation
- Per-monitor static wallpaper support
- Duplicate detection using content hashes

### Download System

- Shared download queue
- Download progress
- Retry and cancellation
- Dedicated Downloads page

### Application

- English and Chinese localization
- Light / Dark / System themes
- System tray integration
- Launch at startup
- Update checking through GitHub Releases
- Persistent settings and local SQLite wallpaper library

## Reliability

This release passed the current validation suite:

- Debug build: 0 warnings / 0 errors
- Release build: 0 warnings / 0 errors
- Automated tests: 190 / 190 passed
- Single-instance behavior verified
- Explorer restart recovery verified
- Dynamic wallpaper Start / Stop verified
- Dynamic wallpaper startup restoration verified
- Static wallpaper regression verified

## What's Next

The next development phase will focus on a redesigned architecture for the future of Muralis, including a more general desktop shell capable of supporting concepts such as custom desktop layouts, widgets, scenes, media surfaces, and deeper desktop personalization.

The existing v0.2.0 release will remain the stable baseline while that work continues.
