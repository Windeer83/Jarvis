# Open-source reuse decisions

This is a throwaway device probe, not a production dependency inventory. The comparison was pinned on 2026-08-31 so the measured result can be traced to concrete source states.

| Project | Commit inspected | License | Probe decision |
|---|---|---|---|
| [TapBlok](https://github.com/cajdata/TapBlok) | `5053c2e101121c0accc4392a929c586e4341b61f` | Apache-2.0 | Reuse the small mechanism: a long first `UsageEvents` lookback prevents an already-foreground app from becoming invisible after service restart, and an active policy schedules a service restart after task removal. Do not import its Room/UI/coroutine application. |
| [OpenLock](https://github.com/MalicKAbdullah/openlock) | `89e74dc450d67d5bf2424defadd7deeddcc6d26f` | MIT | Use its 300 ms polling and documented poll/launch race as a comparison point. The probe keeps a 200 ms interval to measure the best plausible Usage Stats path and records latency rather than assuming it is strong blocking. |
| [Curfew](https://github.com/DavidRodriguez-create/curfew-android) | `4234771bce4d44a4d80e952e102cd4822183acf5` | Apache-2.0 | Use `TYPE_WINDOW_STATE_CHANGED` as the event-latency comparison. Deliberately do not copy settings-screen node traversal, accessibility actions, VPN, or its policy/UI layers; the Jarvis comparison service cannot retrieve window content and does not block. |
| [SelfLock](https://github.com/EtashTyagi/SelfLock) | `57ea0a89cf1749e4b37f3232a4819c0fe61bbf17` | MIT | Use its accessibility-primary/Usage-Stats-backup design and exact-alarm lifecycle as a test reference. Do not adopt the hybrid path before the Mate 70 Pro+ measurements justify the added permissions and complexity. |

No third-party source file, binary, artwork, database layer, UI layer, or license text is copied into this probe. The small Android platform mechanisms above were independently adapted and remain attributable here. GPL projects from the broader research audit are feature references only and were not used.

## Why this is the minimum

- Detection, overlay, foreground service, boot receiver and alarm are Android platform APIs; importing a whole blocker application would add unrelated state, analytics and UI.
- Jarvis-specific policy semantics stay in `PolicyRules` and `PolicyStore`.
- The two detector candidates remain separate during the spike. A hybrid detector is not a default architecture decision.
- The probe declares no network permission, so device evidence stays local and can only be collected through the owner-authorized debug connection.
