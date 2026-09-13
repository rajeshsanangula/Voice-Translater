# Virtual Audio Driver: External Dependency vs. Own Driver

## Decision: **A — continue using an external virtual audio driver (VB-Audio Virtual Cable) for the MVP and near-term roadmap. Do not build a custom kernel-mode audio driver now.**

## What was actually investigated

Windows audio drivers that create a virtual endpoint (a "device" other apps can pick
in a dropdown) are **kernel-mode** components. Shipping one is a materially different
engineering discipline from the rest of this app:

| Requirement | Detail |
|---|---|
| **Driver model** | A virtual audio device needs either a WDM/KS audio miniport driver, or (Windows 10 1809+) an APO/User-Mode Audio driver via the newer Windows Audio Driver framework. Neither is "just C# code" — it's C/C++ against the Windows Driver Kit (WDK), a completely separate toolchain from the .NET/WPF stack this app is built on. |
| **Driver signing** | Since Windows 10 1607+, kernel drivers must be signed with an **EV (Extended Validation) code-signing certificate** and submitted through the **Windows Hardware Dev Center** for Microsoft attestation/WHQL signing before most machines will load them without disabling Secure Boot/test-signing. An EV cert costs real money annually and requires a verified legal business entity — this is not a "generate a cert and go" step. |
| **Installation** | Requires an INF-based driver package, a signed catalog (.cat) file, and (for wide distribution) passing Windows Hardware Compatibility Program certification if you want it to install cleanly on end-user machines without SmartScreen/driver-signing warnings. |
| **Maintenance burden** | Kernel-mode bugs can bluescreen the machine, not just crash a process. It needs its own test matrix across Windows versions/builds, and Microsoft has deprecated/changed the underlying KS APIs before (e.g. WDM audio miniport churn), meaning ongoing maintenance most small teams underestimate. |
| **Security review** | A shipped kernel driver is a materially larger attack surface and a materially larger trust ask of the user (kernel-mode code has full system access) than a user-mode WPF app calling WASAPI. It would need its own dedicated security review, not a shared one with the app. |

## Why VB-Audio Virtual Cable is the right MVP choice

- It's free, widely used (a de facto standard in the streaming/podcasting community for
  exactly this "route app audio into another app" use case), and its installer +
  `.sys` driver files carry **valid Authenticode signatures**, confirmed in this
  session (`Get-AuthenticodeSignature` returned `Valid` for both
  `VBCABLE_Setup_x64.exe` and `vbaudio_cable64_win10.sys`) — installing it did not
  require disabling Secure Boot, test-signing mode, or any Windows security feature.
- It installed cleanly via its normal signed installer and Windows immediately
  recognized the new endpoints (`CABLE Input`/`CABLE In 16ch` as playback devices,
  `CABLE Output` as a capture device) — confirmed via `Get-PnpDevice`, all `Status: OK`.
- It lets this MVP focus engineering effort on what's actually novel here (the
  translation pipeline), not on re-solving a problem VB-Audio already solved well.

## What this means for the product long-term

This is an MVP-stage decision, not a permanent one. If/when this becomes a commercial
product where "install a third-party free tool as a prerequisite" is an unacceptable
onboarding step, revisit building or licensing a purpose-built virtual driver — but
that decision should be made with a dedicated driver-development budget, an EV
signing certificate, and a WDK-experienced engineer, not folded into this app's
general development. Bundling VB-CABLE's redistributable (with attribution/license
compliance) or negotiating a similar arrangement with another signed third-party
driver is a more realistic near-term step than building one from scratch.
