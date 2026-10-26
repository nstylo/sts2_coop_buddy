# Steam Workshop publishing

The installed game's native `ModManager` discovers subscribed items through `SteamUGC.GetItemInstallInfo` and reads the usual DLL/JSON files. No Workshop API calls or extra dependencies are needed in the mod itself.

Publishing uses [Mega Crit's official Mod Uploader](https://github.com/megacrit/sts2-mod-uploader). The helper pins [v0.2.0](https://github.com/megacrit/sts2-mod-uploader/releases/tag/v0.2.0), verifies GitHub's release-asset SHA-256, and chooses the native Mac, Windows or Linux executable. The uploader runs separately from the game and uses the Steam desktop client's logged-in account; no Steam credentials are stored in this project.

## Workspace

```text
workshop/
  workshop.json                 Listing text, visibility, tags and branches
  image.png                     Workshop thumbnail, under 1 MB
  image.svg                     Editable source for the original thumbnail
  mod_id.txt                    Created by a successful upload; keep for updates
  content/CoopBuddy/
    CoopBuddy.dll
    CoopBuddy.json
```

Only `content/` is uploaded as installable mod content. The uploader sends the thumbnail and listing separately. `content/` is generated and ignored by Git. Keep `mod_id.txt` backed up: it identifies the existing Workshop item, and is deliberately outside `dist/` so a clean rebuild will not delete it.

`workshop.json` labels the item **Co-op Buddy**, covers `public` through `public-beta`, declares no dependencies, and starts with `private` visibility. The current shared DLL is verified against public v0.107.1 and beta v0.111.0. Steam supports [branch-specific item versioning](https://partner.steamgames.com/doc/features/workshop/itemversioning): one listing can serve compatible versions to both branches. The [official workspace documentation](https://github.com/megacrit/sts2-mod-uploader/blob/main/template/README.md) notes that branch-range settings can behave inconsistently; verify them on the Workshop page after uploading.

## Prepare and review

From the repository root:

```sh
python3 tools/workshop.py prepare
python3 tools/workshop.py setup
python3 tools/workshop.py upload --dry-run
```

On Windows, use `python` if that is how Python is installed. `prepare` builds Release against the installed game and copies only the DLL and manifest. It preserves your listing edits and saved Workshop ID. `setup` downloads the platform's official uploader into `.tools/ModUploader/`; it does not initialize Steam. If you already downloaded the official ZIP, use `setup --archive path/to/ModUploader-osx-arm64.zip`, or pass `upload --uploader path/to/ModUploader` to use your own installation.

Review `workshop/workshop.json` and `workshop/image.png`. The dry run validates the content, version, thumbnail, visibility and saved item ID, then prints the uploader command. It does not build, download, initialize Steam or upload anything. The helper rejects extra files in `content/`, such as game assemblies, test output or logs.

## Check both branches before a release

Supporting the latest public and beta is the release policy. After updating the two local game installations, run:

```sh
python3 tools/workshop.py check --public /path/to/public/game --beta /path/to/beta/game
```

This rebuilds against public, pins the resulting DLL, and runs the native engine tests on both games using that exact artifact. It restores the public-built package even if a later test fails, and stages it after both pass. On the current Mac, the two preserved installations are `.local/baselines/public-v0.107.1/SlayTheSpire2.app` and `.local/baselines/public-beta-v0.111.0/SlayTheSpire2.app`. Update these baselines when game releases change. Intel/Rosetta, Windows and Linux checks can be run separately as described in the compatibility guide.

After a successful check, use `upload --no-build` to publish the checked package without rebuilding against a different active Steam branch. If future API differences require separate DLLs, retain a working public item revision and upload separate public-only and beta-only revisions to the same Workshop ID; do not drop public support to ship a beta update.

## Create the item

Start Steam and log in to the account that owns Slay the Spire 2 and should own this Workshop item. Then run:

```sh
python3 tools/workshop.py upload --no-build
```

This is the step that submits to Steam. With `--no-build`, it stages the already checked package and invokes the official uploader. Without that option, it rebuilds against the active installation first. With the supplied config it creates a **private** item, writes its ID to `workshop/mod_id.txt` after success, and opens its Steam page. Complete Steam's Workshop agreement if prompted. Review the item before changing `visibility` in `workshop.json` to `public`, `unlisted` or `friends_only` and uploading again. Keep the local visibility setting in sync with changes made on the website, since later uploads apply it again.

## Update the same item

Update the mod version in the project/manifest and update `changeNote` in `workshop/workshop.json`, then run the same `upload` command. Preserve `mod_id.txt`; the uploader uses it automatically. To attach this workspace to an existing item you own:

```sh
python3 tools/workshop.py upload --id YOUR_NUMERIC_WORKSHOP_ITEM_ID
```

The official uploader saves that ID after a successful update. Do not delete it to retry a failed update: without it, a subsequent upload may create a new listing. Uploader diagnostics are in its `.tools/ModUploader/.../mod-uploader.log` directory.

## Verify subscription delivery

Subscribe to the item and let Steam finish downloading it. Move the manually installed `CoopBuddy` directory out of the game's `mods/` folder while the game is closed, launch STS2 normally through Steam, enable the Workshop mod, and restart if prompted. Confirm its source is Steam Workshop and the loaded version is correct. With equal versions, the inspected game loader prefers the manually installed copy, so leaving both installed does not verify Workshop delivery.

For co-op, install the same version on the host and participating players and check viewing, cursors, votes, pick/skip cleanup and reconnects. The local fastmp launcher uses `--force-steam=off`, so it still needs the manual copy; restore that copy before returning to fastmp tests.

Preparation and dry-run checks do not verify Steam authentication, a successful upload, or subscription delivery. Those require a real Workshop item and the logged-in owner's Steam client.
