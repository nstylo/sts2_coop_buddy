# Stable and beta compatibility

Stable/main is the primary release target. As of 2026-10-25, Mega Crit's latest main-branch update is [v0.107.1, Major Update #2](https://steamcommunity.com/games/2868840/announcements/detail/710026912607505281). The opt-in beta is [v0.111.0](https://store.steampowered.com/news/app/2868840/view/671751488532383386). The publisher's Steam announcement feed was checked for newer updates.

The development machine was installed on `public-beta`, confirmed by Steam's `appmanifest_2868840.acf`. The initial implementation and native engine tests therefore used beta assemblies. Those test results do not establish stable compatibility.

| Branch | Version | Current status |
| --- | --- | --- |
| Stable / default | v0.107.1 | Primary target; stable assemblies needed to inspect, compile, and run the compatibility tests |
| `public-beta` | v0.111.0 | Current development artifact; 83 native checks passed on arm64 and Intel Mac runtime |

The manifest intentionally remains at `min_game_version: 0.111.0` until the stable port is implemented and tested. Simply lowering it would let an unverified DLL load into an older game API. The native test harness now passes the actual installed game version into `ModManager.Initialize`, so its loader check also exercises the manifest's version requirement.

## Stable port workflow

1. Obtain the stable installation: Steam → STS2 → Properties → Game Versions & Betas → None. Close the game and let Steam finish downloading. A separate stable installation can instead be passed through `Sts2DataDir` or the test runner's `--game` option.
2. Read `release_info.json`, inspect the reward lifecycle, networking, card serialization/rendering, mod loader, and fastmp APIs in the stable assemblies. Fix differences against the actual API.
3. Build against stable, set the manifest's minimum to the verified baseline, and pass the loader, lifecycle/UI, and DLL portability checks. Check Windows/Linux native builds too when those hosts are available.
4. Run the two-window fastmp test on stable, followed by a normal Steam co-op session.
5. Check the same stable-built DLL against beta before claiming one artifact supports both branches. Keep branch-specific builds only if actual API differences require them.

The beta installation has been preserved in the ignored local directory `.local/baselines/public-beta-v0.111.0/SlayTheSpire2.app` using an APFS copy. It contains game files and must not be committed or included in a mod release. It permits beta regression checks after Steam switches the primary installation to stable.
