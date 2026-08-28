# Mate 70 Pro+ device test record

> Target: PLA-AL10 / HarmonyOS 4.3. Fill this file only with measured results. Do not paste serial number or IMEI.

## Spike 0 — device and install

- [ ] `probe.ps1 device` reports manufacturer, model, Android API, release and build display.
- [ ] APK v1 installs.
- [ ] APK v2 upgrades v1 without uninstalling or losing probe state.
- [ ] Actual installed package IDs and versions are recorded below.
- [ ] Usage access, overlay, notifications, accessibility comparison and battery/background status are visible in the probe.

| App | Expected candidate | Actual package/version |
|---|---|---|
| Douyin | `com.ss.android.ugc.aweme` | pending |
| Bilibili | `tv.danmaku.bili` | pending |
| Xiaohongshu | `com.xingin.xhs` | pending |
| WeChat | `com.tencent.mm` | pending |

## Spike 1 — foreground detection

Usage-event path, 25 automated home launches per target:

| Metric | Result |
|---|---|
| Expected target launches | 100 |
| Detected target resumes | pending |
| Missed launches | pending |
| False target detections | pending |
| P50 detection latency | pending |
| P95 detection latency | pending |
| Maximum detection latency | pending |

Accessibility comparison (measurement only):

| Metric | Result |
|---|---|
| Enabled for comparison | pending |
| Missed launches | pending |
| P95 detection latency | pending |
| Material advantage over usage events | pending |

## Spike 2 — overlay blocking

For each target, perform launches from home, a notification, a deep link and recents. Repeat each available route 25 times. Record whether the opaque blocker appeared before scrolling, playback, chat interaction or other meaningful use.

| App | Home | Notification | Deep link | Recents | Stable bypass? |
|---|---:|---:|---:|---:|---|
| Douyin | pending | pending | pending | pending | pending |
| Bilibili | pending | pending | pending | pending | pending |
| Xiaohongshu | pending | pending | pending | pending | pending |
| WeChat | pending | pending | pending | pending | pending |

- [ ] Block page reads no target-app content.
- [ ] Temporary access refuses an empty reason.
- [ ] A valid reason opens only the current target app for five minutes.
- [ ] The target is covered again after five minutes.
- [ ] Policy expiry removes the overlay within ten seconds.

## Spike 3 — lifecycle and offline

- [ ] A 30-minute policy sent by the Windows pairing probe is persisted and acknowledged.
- [ ] Wi-Fi off: cached policy still blocks and expires locally.
- [ ] Windows stopped: cached policy still blocks and expires locally.
- [ ] Screen off/on: service state and blocking recover.
- [ ] Probe removed from recents: service state and blocking recover or explicitly report unavailable.
- [ ] Huawei power-saving mode: observed behavior recorded.
- [ ] Phone reboot: observed recovery/degraded behavior recorded.
- [ ] Reconnection uploads each outbox event once by event ID.
- [ ] Eight-hour run records background kills, misses and battery delta.

## Verdict

- Gate: **pending** (`pass` / `fail` / `needs explicit accessibility decision`)
- Tested build commit: pending
- Tested APK SHA-256: pending
- Reason: pending
- Smallest validated production mechanism: pending
