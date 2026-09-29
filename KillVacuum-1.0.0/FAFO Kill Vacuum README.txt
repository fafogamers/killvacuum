# Kill Vacuum

**Version 1.0.0**  
**Author:** FAFO Vibin of FAFO Gamers

Kill Vacuum pulls loot off NPC, zombie, and animal corpses into a second backpack. It sits beside the backpack you already wear and keeps a kill streak out of your body, your belt, and your main bag.

Oxide and Carbon both run the same file.

## What it does

- Vacuums scientists, other map NPCs, zombie-horde corpses, and animals.
- Files that loot into one page per Rust item category, plus a Misc page for anything that does not fit.
- Skins animals. Does not skin NPC or zombie bodies.
- Guts whole fish into raw fish, from a corpse or from a catch.
- Keeps food in the buffer cold so it does not spoil.
- Removes the corpse and its body bag after the loot is taken.
- Drops real body-bag loot on the ground when a page is full, and puts a red bar above your hotbar that names the page.
- Never drops worn clothing on the ground. Costumes and other attire are destroyed instead of dumped.

A kill counts from any distance. Loot is pulled only when you walk up to the body. The default range is 6 meters. Set `Max Vacuum Distance (0 = unlimited)` to `0` if you want no range limit.

Turning the vacuum off does not lock the buffer. The button still opens, so stored loot is never trapped.

## What it leaves alone

- Real player corpses and real player body bags.
- Worn outfits on scientists and zombies. The main corpse container is the loot. Belt loot is taken from human scientists. Zombie belt and wear are not.
- Attire that would have spilled because a page was full. That clothing is removed, not dropped.
- Your main backpack plugin. Kill Vacuum is a separate buffer.

## Install

1. Copy `KillVacuum.cs` to `oxide/plugins/` or `carbon/plugins/`.
2. Reload it.

```
o.reload KillVacuum
```

3. Grant the permissions.

```
oxide.grant group default killvacuum.use
oxide.grant group admin killvacuum.admin
```

On Carbon, use the same grants through your permission plugin. The permission names do not change.

The config and the player data file are created on first load.

| File | Where it lives |
|---|---|
| Config | `oxide/config/KillVacuum.json` |
| Player buffers | `oxide/data/KillVacuum.json` |

Carbon uses its own `configs` and `data` folders. Same file names.

A starter config is included in the release zip if you want these defaults before the first boot. If the plugin has already created a config, do not overwrite it. New keys are added the next time the plugin loads.

## Permissions

| Permission | Who | What |
|---|---|---|
| `killvacuum.use` | Players | Vacuum, open the buffer, move the UI, clear their own pages |
| `killvacuum.admin` | Admins | `killvacuum.wipe` from console |

`Admins Bypass Use Permission` defaults to true, so an auth-level admin can use the vacuum without the use permission.

## Pages

Each page is a real large wooden box owned by that player. It is not a fake list. Sort Button can sort the open box. Stack Size Controller's limits apply, because filing uses `Item.MaxStackable()` and the box `maxStackSize` stays at 0.

| Page | What lands there |
|---|---|
| Weapons | Guns and other weapons |
| Ammo | Ammunition |
| Meds | Medical items |
| Food | Food, including gutted fish and skinned meat |
| Resources | Resources |
| Components | Components |
| Construction | Building materials |
| Items | General items |
| Tools | Tools |
| Attire | Clothing that was actual corpse loot, never a worn costume |
| Traps | Traps |
| Electrical | Electrical |
| Fun | Fun items |
| Misc | Anything left over, and any category you turn off |

Turn a category off under `Enabled Category Pages`. Those items fall through to Misc.

A full page does not delete the loot. Body-bag items drop at your feet. Chat also says `Food page is full - LOOT ON GROUND!` and a red bar with white text appears above the hotbar, for example `FOOD PAGE FULL`. The bar stays up for 6 seconds. Wearables are the exception: they are not dropped.

## Player commands

| Command | What it does |
|---|---|
| `/vacuum` | Turn vacuuming on or off |
| `/vacuum on` / `/vacuum off` | Set it directly |
| `/vacuum open` | Open the last page |
| `/vacuum open food` | Open a named page. `med`, `medical`, `guns`, `clothes`, and `mics` are accepted aliases |
| `/vacuum move` | Cycle move mode for the page rail, then the button, then off |
| `/vacuum move button` | Move only the hotbar button |
| `/vacuum move rail` | Move only the page rail |
| `/vacuum move left` / `right` | Snap the piece you are moving to that side of the screen |
| `/vacuum move done` | Save the position |
| `/vacuum move reset` | Put the button and the rail back to the config defaults |
| `/vacuum clear food confirm` | Empty one page. Nothing is deleted without `confirm` |
| `/vacuum clear all confirm` | Empty every page |
| `/vacuum last` | What the last vacuum did |
| `/vacuum status` | On or off, stack count, and range |
| `/vacuum range` | Explains the range rule |

While move mode is on, tap the screen to drop the piece, then use the arrows to fine tune it.

The hotbar button shows the logo if you set one. Under it, `ON` and the stack count are green. `OFF` and the count are red.

## Console

| Command | What it does |
|---|---|
| `killvacuum.wipe <steamid>` | Empty one player's buffer |
| `killvacuum.wipe all` | Empty every buffer. Online players get a fresh empty button |

Server console can run the wipe commands with no extra permission. A player in the client console needs `killvacuum.admin`.

## Wipe day

```json
"Clear Buffer On Map Wipe": true
```

On a new map save, every buffer is emptied, including saved UI positions. Set it to `false` if a map wipe should keep the loot. Reloading the plugin on the same save does not wipe anyone.

`killvacuum.wipe all` is the manual version when you want the buffers gone without changing the map.

## Works with

**Stack Size Controller.** Stacks merge up to the larger of `Item.MaxStackable()` and the item's own stack size before a new slot is used. The buffer boxes do not set their own cap, so they do not fight SSC.

**Sort Button.** Sorting pulls items out and puts them back. Kill Vacuum does not hook `CanMoveItem`, `CanAcceptItem`, `CanStackItem`, `OnItemStacked`, or `OnItemRemove`. A save is deferred, so a sort cannot be written to disk halfway through and come back empty.

**Backpacks.** The open Kill Vacuum page is visible to Backpacks while you are looting it, so a gather filter can still pull from that box into the worn backpack. The page is hidden again when you close it. Kill Vacuum does not yank items out of your inventory to force a gather.

## Config you will actually change

The plugin writes a full `KillVacuum.json` on load. These are the ones that matter.

| Key | Default | What it does |
|---|---|---|
| `Max Vacuum Distance (0 = unlimited)` | `6` | How close you must be before loot is pulled. The kill itself can be from anywhere |
| `Vacuum Delay Seconds` | `1` | How long to wait for the corpse to finish spawning |
| `Slots Per Page` | `48` | Slots in each buffer box. Clamped from 6 to 48 |
| `Harvest Animals` | `true` | Skin animals and file the harvest |
| `Harvest Multiplier` | `1` | Scales animal harvest amounts |
| `Gut Fish In Corpses` | `true` | Gut whole fish found in a corpse |
| `Gut Fish On Catch` | `true` | Gut a fish as you catch it, when vacuum is on |
| `Remove Corpse After Vacuum` | `true` | Delete the body and the body bag after the loot is taken |
| `Vacuum NPC Backpacks` | `true` | Also take an NPC backpack drop. Skipped for zombies while zombie loot only is on |
| `Zombie Corpse Loot Only` | `true` | Zombies give the main corpse container only. No belt, no worn costume, no attire mixed into that container, and zombies are not skinned |
| `Refrigerate Food` | `true` | Food in the buffer does not spoil |
| `Drop Buffer On Death` | `false` | Drop the whole buffer at your corpse if you die |
| `Warn When A Page Is Full` | `true` | Chat line plus the red bar above the hotbar |
| `Clear Buffer On Map Wipe` | `true` | Empty every buffer when the map save is new |
| `Chat Notify On Vacuum` | `false` | Extra chat line on each successful vacuum |
| `Credit Owned Turrets And Traps` | `false` | Also vacuum kills from your own turrets and traps |
| `Admins Bypass Use Permission` | `true` | Auth admins can use it without `killvacuum.use` |
| `Button Image URL` | empty | Direct `https://` link to a png or jpg. The image fills the button. Leave it blank for the plain VAC button |
| `Zombie Keywords` | see config | Extra name fragments that count as a zombie |
| `Animal Keywords` | see config | Extra name fragments that count as an animal |
| `Fish Yield By Shortname` | see config | How much raw fish each whole fish becomes |
| `Enabled Category Pages` | all on | Turn individual category pages off. Disabled pages fall through to Misc |

`Harvest All Bodies` is still in the config file. NPC and zombie bodies are not skinned. Only animals are.

`Button Image URL` has to be a direct image link, `https://` only. A web page link is ignored.

## Button art

The release includes two images.

| File | Use |
|---|---|
| `branding/KillVacuum-Poster.jpg` | Full poster, dark background |
| `branding/KillVacuum-Thumbnail.png` | Transparent mark for the in-game button |

Host the thumbnail somewhere that serves the png directly, paste that URL into `Button Image URL`, and reload.

## Credits

Kill Vacuum is by FAFO Vibin of FAFO Gamers.

Built for a private Oxide/Carbon server, then cleaned up so another owner can run it without a tour of the source.
