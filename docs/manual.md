# Workshop Procgen

Workshop Procgen is a BepInEx plugin for Dungeon Alchemist. It does two jobs.

Subscribed Steam Workshop assets can stand in for the props, floors, walls, doors, and windows that room generation places. You choose which assets are allowed, how often they appear, and what kind of block each one builds.

You can also pick several objects at once and paint or place them together.

Roofs stay on the game's own roof pieces. Workshop models cannot replace them.

The plugin loads with the game. Open a map and wait a few seconds after launch so Workshop files can finish importing. Press **F6** for the asset list, then **Manual** to read this guide inside the game. Hotkeys can be changed in `BepInEx/config/com.aedin.workshopprocgen.cfg`.

## Workshop assets in room generation

Press **F6** to open Workshop Procgen. The window can be dragged by its title bar. **Close**, or Escape, hides it.

**Replace props during room generation** turns the whole substitution on or off.

**Default rate** is the chance that a matching native piece is swapped for one of your assets. It starts at 30%. Drag the slider, then release the mouse. A single asset can override that rate with **Set rate**. **Default** on that row puts it back on the global rate.

**Also replace props that already have items on them** lets a dressed table or similar piece be swapped. Leave it off when the items on top should stay with the original prop.

A newly subscribed asset is included as soon as the game imports it. You do not have to add it to a list. Clear its checkbox when you do not want generation to use it.

### Choosing what is used

**Select all** and **Unselect all** change the assets listed under them. Search, category, the Selected filter, and the current folder all limit that list. Unselect all on the full list, then turn on only the assets you want.

**Show** filters the list to everything, only checked assets, or only unchecked assets.

**Sort** is A–Z or Newest. Newest uses the Workshop folder's creation time.

**Category** limits the list to one replacement category, such as Tables or full walls.

**Search** matches the asset name and its role.

### Folders

New assets stay in **Unassigned**. **Add folder** creates a folder and shows it. Open an asset's **Folder** button, or use **Move shown**, to put the listed assets into a folder. **All** shows every folder as its own section.

Rename and **Delete folder** appear while a folder is open. Deleting a folder puts its assets back in Unassigned. It does not unsubscribe them or remove them from the game.

### What each asset replaces

Each row shows the asset's role, Workshop id, and size.

**Categories** edits which native categories an object may fill. Desks also fill table slots, chairs also fill stools, and crates also fill chests, until you edit that asset. **Use automatic categories** restores that behavior.

**Build as** decides the kind of block:

- **Auto** leaves a normal object as an object. A single wall panel whose name says wall, and whose mesh is about one meter wide and one story tall, is registered as a full wall on its own.
- **Object** keeps it a prop even if the name says wall.
- **Wall**, **Partial**, **Floor**, **Door**, and **Window** build that asset as that block and fit the mesh onto one segment or one floor tile. A wide wall is scaled down to one meter. A short wall set to **Partial** keeps a lower height.

An explicit Build as removes the asset from the prop pool. An auto-detected wall panel can still be used as a prop and as a wall.

Floors, full walls, partial walls, doors, and windows replace native pieces of that same kind. An asset set to Wall with no custom category list can replace every full wall. Edit its categories when it should only replace some of them.

Very large meshes, on the order of tens of thousands of vertices, still render, but the game's door and window cutters are skipped for that mesh. Holes are not carved into it. A mesh that cannot be built at all is skipped, the room stays up, and the feedback form can open with the asset name filled in.

### Saving

The menu writes `BepInEx/config/WorkshopProcgen.json`. The game picks up that file on its own. Restart the game after you copy in a new plugin DLL so the new version actually loads.

## Painting with more than one asset

Multi-select painting uses the object picker. It is separate from room generation.

1. Open the object picker.
2. Hold **Ctrl** and click an object to add it. Ctrl-click it again to remove it.
3. A list titled **Paint set** appears at the upper right while the set is not empty. **F8** hides or shows that list. **F7** clears the set.

Right Ctrl works the same as left Ctrl. The key can be changed in the config file under `SelectModifier`.

With two or more assets in the set:

- The place-object brush switches to a random member of the set after each placement.
- Dragging out a line or an area picks a random member for each spawned object.
- On the Paint Objects tab, **Create Brush From Selected Object** paints the whole set instead of the single object.

Those three behaviors are on by default. They are `RandomizePlaceBrush`, `RandomizeDragPlacement`, and `OverridePaintCustomBrush` in the config file. Set `MultiPaintEnabled` to false to turn multi-select off.

The paint set lasts for the session. It is not written into the map.

## Feedback

**Feedback** on the F6 window opens a note to the mod author. If a Workshop floor, door, or window fails to build, the same form can open on its own with that failure written in. Opening the form does not send anything.

Press **Send** when the note is ready. **Don't send** closes the form. A sent note contains the text you typed and the mod version. It does not include your map, and it does not go to the people who make Dungeon Alchemist.

When the game itself shows its bug report, use that report for the game. Use the mod form only for Workshop Procgen. The form does not close, replace, or fill in the game's report.

The note needs an internet connection. A copy of the mod with no delivery address keeps the note on this computer. The first **Send** from a configured copy makes the delivery service email the mod author a confirmation link. The note waits until that link is opened. Check the spam folder if the confirmation is not in the inbox. Later notes arrive on their own.

## Revert points

Two git checkpoints exist in this project.

`checkpoint/objects-only` is the mod when it only swapped props and supported multi-paint.

`checkpoint/procgen-0.5.3` is the later mod: structural swaps, fitted walls, floors, doors, windows, and the F6 list, before the feedback form.

Check out the branch you want to return to. Test maps named `WorkshopProcgenTest.dam` are not part of either checkpoint.
