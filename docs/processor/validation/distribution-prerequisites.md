# Distribution prerequisites — Stage 0

Inventory date: 2026-10-19. This is not installer acceptance.

## Accepted owner policy

Personal testing first, no purchases, all Android/Windows/Linux/macOS targets retained. Where paid publisher credentials are unavailable, documented unsigned/ad-hoc test installers are already approved. Do not ask again solely for those missing credentials; package integrity and actual native execution still must pass.

## Observed access

| Area | Observation / remaining gate |
| --- | --- |
| Windows | Local Windows x64, .NET SDK 10.0.303/runtime 10.0.13, CMake and Ninja available. No CurrentUser code-signing certificate/private key found. NSIS not found on PATH; obtain a pinned official build at packaging stage. |
| Android signer | Prior release certificate hash recorded in baseline audit. Repository secret names inventory contains the protected upgrade keystore secret; no value was read or copied. Verify the actual final CI APK independently. |
| Apple signing | No Apple signing/notarization secrets configured in this repository. No local macOS credential access established. Use accepted accurately labeled personal-test packaging unless authorized credentials later become available. |
| Native CI | Public repository Actions enabled. Standard Windows, Linux, Apple Silicon and Intel Mac runners are available for the feasibility matrix. A selected runner is not a native passing result. |
| Linux distribution | Ubuntu 24.04 native CI path prepared. Fedora/rpm install/uninstall environment still must be established; do not represent Ubuntu inference as Fedora package acceptance. |
| macOS distribution | Both architecture probe jobs prepared; actual app/pkg/tray/startup/upgrade/uninstall tests remain pending. Supported target intersection must include .NET 10's macOS support. |
| LAN interoperability | Not established. Separate hosted CI machines do not prove same-LAN discovery/pairing; a Windows + non-Windows network rig and final S23 checks remain required. |
| Physical Android | Deferred to final acceptance per latest owner instruction. Last connection was not independently verified; emulator-only tests are not S23 acceptance. |

No publisher keys were created, purchased, exported or installed. No OS security setting has been disabled. Do not automate a Gatekeeper/browser trust bypass to turn an unsigned package into a passing launch.

## Primary references

- [.NET 10 supported systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Standard GitHub runner architectures](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [Apple notarization](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
- [NSIS license](https://nsis.sourceforge.io/License)
