# Mate 70 Pro+ device test record

> Target: PLA-AL10 / HarmonyOS 4.3. Fill this file only with measured results. Do not paste serial number or IMEI.

## Spike 0 — device and install

- [x] `probe.ps1 device` reports manufacturer, model, Android API, release and build display.
- [x] APK v1 installs.
- [x] APK v2 upgrades v1 without uninstalling or losing probe state.
- [x] Actual installed package IDs and versions are recorded below.
- [x] Usage access, overlay, notifications, accessibility comparison and battery/background status are visible in the probe.

| App | Expected candidate | Actual package/version |
|---|---|---|
| Douyin | `com.ss.android.ugc.aweme` | `com.ss.android.ugc.aweme` / 39.8.0 |
| Bilibili | `tv.danmaku.bili` | `tv.danmaku.bili` / 9.8.0 |
| Xiaohongshu | `com.xingin.xhs` | `com.xingin.xhs` / 9.44.1 |
| WeChat | `com.tencent.mm` | `com.tencent.mm` / 8.0.77 |

## Spike 1 — foreground detection

Usage-event path, 25 automated home launches per target:

| Metric | Result |
|---|---|
| Expected target launches | 100 |
| Detected target resumes | 100 (25 per target) |
| Missed launches | 0 |
| False target detections | 0 unmatched target events in the controlled run |
| P50 detection latency | 163 ms |
| P95 detection latency | 1,610 ms |
| Maximum detection latency | 1,617 ms |

The automated run observed 61 blocker state transitions. This is not a 61/100
success rate: when the full-screen blocker remained visible between repeated
launches of the same package, the probe deliberately did not log another state
transition. Route-level blocking success is measured separately in Spike 2.

Accessibility comparison (measurement only):

| Metric | Result |
|---|---|
| Enabled for comparison | Yes; bound before the fair comparison run |
| Missed launches | 23/100 (all repeated Xiaohongshu launches) |
| P95 detection latency | 109 ms for the 77 matched launches |
| Material advantage over usage events | No; lower matched-event latency did not offset misses and duplicate events |

The fair comparison run kept the accessibility service bound and did not use it
for blocking. Usage events again matched 100/100 launches with 0 misses (P50 106
ms, P95 193 ms, maximum 236 ms). Accessibility emitted 122 target window
events, but only 77 could be uniquely matched to the 100 launches; 45 were
additional events and 23 Xiaohongshu launches had no matching window event.
Huawei also disabled the accessibility service after an earlier app force-stop.
The smallest production candidate therefore remains Usage Stats plus a normal
application overlay; the accessibility comparison must not enter the formal
skeleton.

## Spike 2 — overlay blocking

For each target, perform launches from home, a notification, a deep link and recents. Repeat each available route 25 times. Record whether the opaque blocker appeared before scrolling, playback, chat interaction or other meaningful use.

| App | Home | Notification | Deep link | Recents | Stable bypass? |
|---|---:|---:|---:|---:|---|
| Douyin | 25/25 blocked | unavailable: no active notification | 25/25 blocked | 25/25 blocked | None in tested routes |
| Bilibili | 25/25 blocked | unavailable: no active notification | 25/25 blocked | 25/25 blocked | None in tested routes |
| Xiaohongshu | 25/25 blocked | unavailable: no active notification | 25/25 blocked | 25/25 blocked | None in tested routes |
| WeChat | 25/25 blocked | unavailable: no active notification | 25/25 blocked | 25/25 blocked | None in tested routes |

The deep-link run used each installed app's resolved custom scheme and matched
100/100 launches to both a usage event and a blocker event. Maximum observed
host-command-to-block time was 1,217 ms. The recent-task run used the front
Huawei recents card and matched 100/100 restores to blocker events; the maximum
usage-event-to-block time was 254 ms. The notification service reported zero
active notifications for all four target packages, so no real notification
content or synthetic substitute was used. On 2026-09-01 the owner explicitly
excluded this route from the personal V2-A skeleton gate because notification
behavior was not considered worth further test effort. These cells are
therefore **N/A by owner waiver**, not passes.

- [x] Block page reads no target-app content.
- [x] Temporary access refuses an empty reason.
- [x] A valid reason opens only the current target app for five minutes.
- [x] The target is covered again after five minutes.
- [x] Policy expiry removes the overlay within ten seconds.

The empty-reason attempt kept the blocker visible and wrote no temporary-access
event. A test reason granted only Douyin; Bilibili remained blocked during that
window. Douyin was covered again 300,148 ms after the grant. A separate
one-minute policy delivered its exact expiry alarm 21 ms after the scheduled end,
stopped the foreground service and removed the application-overlay window.

## Spike 3 — lifecycle and offline

- [x] A locally confirmed 30-minute policy is persisted; no Windows process is required during execution.
- [x] Wi-Fi off: cached policy still blocks and expires locally.
- [x] Windows stopped: cached policy still blocks and expires locally.
- [x] Screen off/on: service state and blocking recover.
- [x] Probe removed from recents: service state and blocking recover or explicitly report unavailable.
- **N/A by owner waiver** — Huawei power-saving mode was not measured.
- [x] Phone reboot: persisted policy, alarm, service and blocking recovery were measured.
- **N/A by owner waiver** — the eight-hour run was not performed.

The probe has no Windows runtime or network permission; confirmed policies were
stored in app-private preferences and executed locally. With Wi-Fi disabled, a
target remained blocked and a one-minute policy expired 21 ms after its
scheduled end; Wi-Fi was restored to its prior on state. During a 20-second
screen-off interval the policy and foreground service remained active, and a
target was blocked after wake. Swiping the probe's actual Huawei recents card
away scheduled one service restart, kept the policy active and subsequently
blocked another target. A shell request for standard battery saver was rejected
while USB power was connected (the system continued to report OFF), so that
attempt is not counted as a power-saving result.

For the reboot run, an active 30-minute policy blocked Douyin before the reboot
command. The device boot count changed from 40 to 41. After restart, the same
policy ID and original end epoch remained persisted. The probe recorded
`BOOT_COMPLETED` at 2026-09-01 08:59:09.489 +08:00, rescheduled the exact expiry
11 ms later to the unchanged end epoch, started the foreground service 100 ms
after the recovery attempt, and reported the service available after 129 ms.
Recovery was automatic and occurred before the probe activity was opened.

The boot-recovery event occurred 109,437 ms after the host issued `adb reboot`;
that interval includes shutdown, system boot, first unlock and Huawei broadcast
delivery, so enforcement must be treated as unavailable until the recovered
service reports healthy. After recovery, one launch each of Douyin, Bilibili,
Xiaohongshu and WeChat produced four usage events and four blocker events. The
measured usage-detection-to-block intervals were respectively 179, 50, 52 and
99 ms. The rebuilt alarm was present at the original policy end time; its firing
was not re-waited in this run because local exact expiry had already been
measured separately at 21 ms late. The owner then stopped the probe policy
through its normal UI, and the service was destroyed with no active policy left.

## Verdict

- Gate: **accepted for the production skeleton with documented owner waivers**
- Tested build commit: `6f1dc7f`
- Tested APK SHA-256: `D1AD359CD6A4849051B67B09CE5BC0F97989997F10F21B9142D5446138A99C98`
- Reason: the preferred ordinary-app path detected 100/100 controlled target
  launches, blocked every measured home/deep-link/recents route, enforced
  temporary access and local expiry, survived offline/screen-off/recents
  removal, and automatically recovered an active policy after a real device
  reboot. The real-notification route, Huawei power-saving mode and eight-hour
  run remain deliberately unmeasured owner-accepted risks; they are not passes.
- Smallest validated production mechanism: `UsageStatsManager` foreground
  detection + opaque `TYPE_APPLICATION_OVERLAY` blocker + visible foreground
  service + persisted local policy + exact expiry alarm + `BOOT_COMPLETED`
  alarm/service reconstruction. The accessibility comparison is rejected.
