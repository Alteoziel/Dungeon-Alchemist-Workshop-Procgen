# Dungeon Alchemist Workshop Procgen

Workshop Procgen is a plugin for the Windows Steam version of Dungeon Alchemist. Subscribed Steam Workshop assets can stand in for the props, floors, walls, doors, and windows that room generation places. You choose which assets are used and how often. You can also pick several objects at once and paint with them.

This release works with **Dungeon Alchemist 1.8.10** on Windows through Steam. That is the patch notes version the game reports. It was made for that build, which runs on Unity 2022.3.62f2.

Roofs stay on the game's own roof pieces.

## Install

1. Close Dungeon Alchemist.
2. Download [WorkshopProcgen-0.5.5.zip](https://github.com/Alteoziel/Dungeon-Alchemist-Workshop-Procgen/releases/download/v0.5.5/WorkshopProcgen-0.5.5.zip).
3. Open your Downloads folder. Right-click the zip, choose **Extract All**, then **Extract**.
4. Open the new folder and double-click **Install Workshop Procgen**.
5. If Windows asks you to confirm, choose **Run**, then **Yes**.
6. When the installer shows your game folder, choose **Yes**.

Start Dungeon Alchemist, open a map, and press **F6**. Choose **Manual** in that window for the rest of the guide.

The installer copies files into the game folder. It does not change your maps. If it cannot find the game, it asks you to select the folder yourself. In Steam, open the game page, choose the gear icon, **Manage**, then **Browse local files**, and select that folder.

## Using it

Press **F6** to choose which Workshop assets room generation may use. **Manual** in that window explains folders, rates, and how to build an asset as a wall, floor, door, or window.

To paint with more than one object, open the object picker, hold **Ctrl**, and click each object. **F7** clears that set. **F8** hides the list.

## Remove

Close the game. Delete the folder `BepInEx\plugins\WorkshopProcgen` inside the Dungeon Alchemist folder. The game then runs without this mod.


Disclaimer: this mod was made utilizing AI.
