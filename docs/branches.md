# Public and beta release support

Every release targets **both the latest public branch and latest public beta**. As checked against Mega Crit's Steam announcement feed on 2026-10-25, these are [public v0.107.1](https://steamcommunity.com/games/2868840/announcements/detail/710026912607505281) and [beta v0.111.0](https://store.steampowered.com/news/app/2868840/view/671751488532383386).

| Branch | Game version | v0.6.0 engine results |
| --- | --- | --- |
| Public/default | v0.107.1 | 86 passed on Apple Silicon and Intel/Rosetta |
| public-beta | v0.111.0 | 87 passed on Apple Silicon and Intel/Rosetta |

All four runs use the same DLL compiled against public. The manifest minimum is `0.107.1`, and the Workshop range is `public` through `public-beta`. The public run skips beta's transport handshake test because that API did not exist in public; both exercise native loading, gameplay-message ID stability, vanilla receivers, reward lifecycle and UI.

## Release workflow

1. Check the publisher's current public and beta releases. Keep an installed copy of each, including matching assemblies, engine and PCK.
2. Inspect actual API changes. Build against public as the baseline; keep compatibility adjustments small and verify them against both real assemblies.
3. Run `python3 tools/workshop.py check --public /path/to/public/game --beta /path/to/beta/game`. It tests the exact public-built artifact on both branches and stages that artifact for upload.
4. Run native platform checks where available and interactive fastmp/Steam co-op checks, including a modded client with an unmodded host and the reverse. Players must share the same game version within a session; supporting two branches does not bypass STS2's game-version check.
5. Publish the checked package with `python3 tools/workshop.py upload --no-build`, using the existing Workshop ID.

If a later beta requires incompatible APIs, keep serving the working public build and upload separate branch-specific revisions under the same Workshop item. Steam's [item versioning](https://partner.steamgames.com/doc/features/workshop/itemversioning) can deliver the appropriate revision for each player's game branch. Never replace public support with a beta-only release.

The ignored local baselines currently contain both `.local/baselines/public-v0.107.1/SlayTheSpire2.app` and `.local/baselines/public-beta-v0.111.0/SlayTheSpire2.app`. These are private APFS copies of game files, not redistributable mod content. The primary Steam installation is currently public.
