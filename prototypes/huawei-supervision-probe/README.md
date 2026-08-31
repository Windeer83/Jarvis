# Huawei supervision probe

> **THROWAWAY PROTOTYPE — do not use as the production mobile architecture.**

## Question

On the owner's Huawei Mate 70 Pro+ (PLA-AL10, HarmonyOS 4.3), can an ordinary privately sideloaded Android APK use `UsageStatsManager` plus `TYPE_APPLICATION_OVERLAY` to detect and cover Douyin, Bilibili, Xiaohongshu, and mobile WeChat before meaningful interaction, keep an already-confirmed policy running while Windows or Wi-Fi is unavailable, and release it at the original end time? The accessibility service in this prototype is a measurement-only comparison: it records package/window timestamps and never reads the node tree or drives blocking.

This branch is the primary source for GitHub issue #31. Only the measured verdict and the smallest validated mechanism should be carried into production.

The pinned open-source comparison and the exact pieces adopted or rejected are recorded in [OPEN-SOURCE-REUSE.md](OPEN-SOURCE-REUSE.md).

## Privacy boundary

The probe records only:

- device manufacturer/model, Android API and build display;
- installed target package names and versions;
- foreground package identifiers and event/detection timestamps;
- policy start/end, block, temporary-access and availability events;
- the temporary-access reason entered by the owner.

It does not read notifications, UI text, chats, screenshots, recordings, keyboard input, contacts, IMEI, serial number, or advertising identifiers.

## One-command entry point

From this directory in PowerShell:

```powershell
.\probe.ps1
```

The script bootstraps the local build tools (after the owner accepts the Android SDK license), builds two same-signature debug APK versions, and then prints the next device command. Tooling is stored under the repository's ignored `.tools` directory.

For mainland-China connectivity, Android SDK packages come from Google's official `googledownloads.cn` repository and the command-line-tools archive is checked against Google's published SHA-256. Gradle obtains Google Maven artifacts through Alibaba Cloud's mirror because the canonical host stalls on the probe machine; no mirror artifact is committed to the repository.

Once the phone is connected and has authorized USB debugging:

```powershell
.\probe.ps1 device
```

The device phase installs v1 then v2, opens the probe, prints the non-sensitive device/API facts and enumerates the four target packages. Permission grants and Huawei background settings remain explicit owner actions.

The owner-facing sequence is in [PHONE-STEPS.md](PHONE-STEPS.md).

After permissions are enabled and a 30-minute probe policy is active:

```powershell
.\probe.ps1 benchmark
```

This runs 25 home-screen launches for each target package. Notification, deep-link and recent-task launches remain a manual matrix because those entry points depend on the installed app/account state.

Collect and analyze results:

```powershell
.\probe.ps1 collect
```

## Pass gate

The production skeleton remains blocked until `DEVICE-TEST.md` is fully measured. In particular, a pass requires no missed target detection in 100 foreground switches, every target entry route covered before meaningful interaction, expiry within 10 seconds without Windows, explicit degraded state when permissions/service are unavailable, and an eight-hour lifecycle run. Any stable ordinary-user bypass or frequent late detection is a fail, not a reason to rename a notification as blocking.
