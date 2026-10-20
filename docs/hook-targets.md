# Hook targets (Dungeon Alchemist, Unity 2022.3.62f2 Mono)

Source: `decompiled/Assembly-CSharp/` (ilspycmd 9.1, run `tools/decompile.ps1`). Paths below are relative to that folder.
All types are in `Assembly-CSharp.dll` unless noted. `SeedRandom` is in `SharedAssembly.dll` (already referenced by the csproj).

## TL;DR for the generator-swap worker

- There is **no `RoomGenerator.GenerateRoom`**. Entry is `ThreadedMapEditor.GenerateRoom` -> `MapEdit.AddRoomAndGrowMap` -> `MapManipulator.PopulateRoom` -> `RoomGenerator.Generate`.
- The whole room-generation pipeline is plain managed C#. **No Burst and no Unity Jobs anywhere in `DungeonAlchemist.RoomGeneration`.** Burst is only used in terrain, roofs, ceilings, and polygon clipping.
- Generation runs on a **dedicated background `System.Threading.Thread`** (`RoomGenerationThread`), against a separate `Map` copy. It also uses `Parallel.ForEach` for candidate scoring. The `DatabaseConnection` is **shared with the main thread**.
- `ThemeConfig` does **not** pick blocks. Native blocks come from the room's example **analysis**: `RoomConfig` -> `RoomAnalysisData.objectAnalysis.occurences` (`ObjectOccurenceData`, keyed by `BlockOrCategoryId`).
- **Recommended swap point:** a prefix on `MapManipulatorRoomGenratorTarget.SpawnObjects(GeneratedObjectData)` (the game spells it "Genrator"). That call is where the validated generator output is committed to the map, and it does a collision check. Swap `template.source` with `UpdateBlockId(...)` and fall back to native if placement fails. Details are below.
- **Do not** swap inside the `PotentialCandidate` ctor. `PotentialCandidateSelector.CalculateScore` throws `ArgumentException` when `candidate.config.GetAICategoryId(db)` is not in the analysis occurrences, and Workshop configs normally have no AI category.

## 1. Generation entry, threading, completion

| What | Signature | File |
|---|---|---|
| UI entry | `IPendingOperation<AddRoomOperation> MapEditor.AddRoom(RoomConfig roomConfig, AIConfig AISettings, FoundationDataPartSet partSet)` | `DungeonAlchemist.MapEditing/MapEditor.cs:271` |
| Thread dispatch | `PendingAddRoomOperation ThreadedMapEditor.GenerateRoom(RoomConfig roomConfig, AIConfig AISettings, FoundationDataPartSet tileSet)` and `protected void ExecuteInThread<T>(PendingOperation<T>, ExecuteMapEdit<T>)` | `DungeonAlchemist.RoomGeneration/ThreadedMapEditor.cs:38,20` |
| Other entries (same path) | `UpdateRoomConfig`, `AddPartsToRoom`, `RemovePartsFromRoom` (Expand), `ClearRoom` | same file |
| Worker thread | `class RoomGenerationThread`: `Start(DatabaseConnection)` creates `new Thread(Run)` and runs a `ConcurrentQueue<Action>` loop | `DungeonAlchemist.RoomGeneration/RoomGenerationThread.cs` |
| Threaded vs main | `MapEditor.threaded = true` by default (forced false only in the Unity editor). `ThreadedMapMemoryStructure` is threaded; `MainThreadMapEditor` is the fallback | `MapEditor.cs:51,125` |
| Populate | `private void MapManipulator.PopulateRoom(RoomInstanceData room, PreviousFoundationState prevMapState, AIConfig AISettings, IProgressListener progressListener)` and `private void ExpandRoom(...)` | `DungeonAlchemist.Data/MapManipulator.cs:715,740` |
| Generator | `public RoomGenerator(DatabaseConnection, RoomConfig, RoomAnalysisData, AnalysisMetadata, bool crashOnInvalidExample)` | `DungeonAlchemist.RoomGeneration/RoomGenerator.cs:38` |
| Generate | `public void RoomGenerator.Generate(Foundation foundation, RoomInstanceData room, PreviousFoundationState prevMapState, AIConfig settings, IRoomGeneratorTarget target, IProgressListener progressListener)`. `Expand(...)` has the same shape plus `ExistingRoomAnalysis, List<RoomInstancePartData>` | `RoomGenerator.cs:68,73` |
| Core | `private void RunGeneration(..., Func<IProceduralLayer, ProceduralGenerationData, IFuture> executeLayer)` builds `SeedRandom rand`, runs every layer, then calls `IFuture.Execute()` synchronously | `RoomGenerator.cs:78` |

**Generation-complete points** (all on the generation thread):
- Per object layer: `IRoomGeneratorTarget.SpawnObjects(GeneratedObjectData)`, called from the `future.After` callback in `ObjectsProceduralLayer.GenerateObjectsWithAnalysis`.
- Whole room: a postfix on `RoomGenerator.Generate` / `RoomGenerator.Expand`. Their futures run synchronously, so the room is complete when the method returns.
- Main-thread commit: `ExecuteInThread` records into a `MapActionQueue` checkpoint (`ApplyLastCheckpoint(pendingOp)`, then `Undo()` on the thread map). `MapEditor` replays that on the main map as one undoable action (`mapEditorThread.Complete(pendingOp, PrepareAddRoomAction)`). So swapped objects get normal undo and save behavior for free.

## 2. Random seed

- `RoomGenerator.RunGeneration`: `int num = RandomUtility.GetInt();` In the editor only, this is overridden from `PlayerPrefs["LastGeneratedRandomSeed"]`. It is stored in **`public static int? RoomGenerator.lastGeneratedRandomSeed`** before the layers run, then `new SeedRandom(num)` is passed as `ProceduralGenerationData.rand` to every layer.
- `public static bool SaveLastGenerationRandomSeed()` and `ClearLastGenerationRandomSeed()` only write PlayerPrefs. In release builds every generation gets a fresh random seed.
- Each `PotentialCandidate` gets `new SeedRandom(rand.GetInt())`; `BuildingBlockInstanceDataTemplate.randSeed` defaults to `RandomUtility.GetInt()`.
- **Mod RNG:** derive it from `lastGeneratedRandomSeed` plus a per-room spawn counter. **Never consume the game's `SeedRandom`**, because extra draws change the whole layout.
- `SeedRandom` API (SharedAssembly): `SeedRandom(int)`, `float Value`, `bool Roll(float)`, `int GetInt()`, `int Range(int,int)`, `float Range(float,float)`, `T Pick<T>(IEnumerable<T>)`, `T PickWeighted<T>(IList<T>, GetWeightedScore<T>)`, `List<T> Shuffle<T>(IList<T>)`.

## 3. Object layer pipeline

```
ObjectsProceduralLayer.Generate(ProceduralGenerationData) : IFuture                     ObjectsProceduralLayer.cs:30
  -> GenerateObjectsWithAnalysis(data)                                                   :48
       TemporaryActionQueue.Start(); new ObjectGenerator(db, room, foundation, analysis.objectAnalysis, config, data.rand, ...)
       spawners = config.GetSpawnersConfigList(db, analysis).CreateSpawners(...)         (ObjectsProceduralLayerConfig.cs:84)
       IObjectSpawner.PrepareTask(...)   e.g. PotentialCandidateSelectorObjectSpawner.PrepareTask
         -> ICandidateObjectSpawner.SpawnObject(int idx, float mult)                     CandidateObjectSpawner.cs:47
              ObjectPicker.SelectNextObject(occurences, existing, invalidIds, mult, log) : ObjectOccurenceData   ObjectPicker.cs:95
              -> protected abstract bool Generate(ObjectOccurenceData, IObjectGenerator, LogFile)
                 CandidateSetObjectSpawner<T>.Generate -> private bool EvaluateAndSpawnCandidate(LogFile log, List<T> candidates)   CandidateSetObjectSpawner.cs:34
                    SortCandidates -> PotentialCandidateSelector.SortCandidates(...)  (scores; needs AI category in occurrences!)
                    generator.CandidateEvaluator.EvaluateCandidates(PotentialCandidateSet, log) -> List<PlaceCandidate>
                    generator.SpawnCandidate(placeCandidates.First().GetGeneratedObject(), log)
       future.After: TemporaryActionQueue.Stop()  // REWINDS everything placed during simulation
                     data.target.SpawnObjects(objectGenerator.GetGeneratedObjects())   // real placement
```

Key signatures:
- `class ObjectsProceduralLayer : ProceduralLayer<ObjectsProceduralLayerConfig>`: `public override IFuture Generate(ProceduralGenerationData data)`, `public override IFuture Expand(ProceduralGenerationData, ExistingRoomAnalysis, List<RoomInstancePartData>)`, `public override IEnumerable<IObjectSpawner> CreateSpawners(bool allowLights)`.
- `public class PotentialCandidateSelector` (`PotentialCandidateSelector.cs`):
  - ctor `(DatabaseConnection, ObjectGenerator, RoomProfile, SeedRandom, LightMap, IDictionary<BlockOrCategoryId, ObjectOccurenceData>)`
  - `public List<PotentialCandidate> SortCandidates(List<PotentialCandidate> candidates, LogFile log, bool spawnCloseToColumns)`, which uses `Parallel.ForEach`
  - `private float CalculateScore(PotentialCandidate, IReadOnlyList<GeneratedColumn>, LogFile)` throws if the AI category is unknown.
- `private bool CandidateSetObjectSpawner<T>.EvaluateAndSpawnCandidate(LogFile log, List<T> candidates) where T : PotentialCandidate`. This class is **generic**, so Harmony must patch each closed type (`AccessTools.Method(typeof(CandidateSetObjectSpawner<X>), ...)`). Prefer the non-generic points below.
- **`SpawnCandidates` exists only as** `private bool TableGridSpawner.<nested>.SpawnCandidates(LogFile log, List<PlaceCandidate> placeCandidates)` (`TableGridSpawner.cs:584`). All spawners funnel into the next item instead:
- **`public bool ObjectGenerator.SpawnCandidate(GeneratedObjectCandidate candidate, LogFile log)`** (`ObjectGenerator.cs:226`, also on `IObjectGenerator`). This is the common commit for every spawner. It places into the simulation map with `objectManipulator.AddObjectWithoutManipulation` (**no collision check**), records `GeneratedObjectInstanceData`, and appends `candidate` to `generatedObjects.objects`.
- `CreateReplacementPotentialCandidate` is `private PotentialCandidate NoWallLightOrientedPatternGenerator.CreateReplacementPotentialCandidate(PotentialCandidate candidate, LogFile log)` (`:89`). It only swaps in a light config and is not a general hook.
- `private PlaceCandidate PotentialCandidateEvaluator.GetPlaceCandidate(PotentialCandidate potentialCandidate, int generatedVariantIndex, bool resetBlockingGrid, LogFile log)` (`PotentialCandidateEvaluator.cs:414`). This is where a candidate is collision-validated, using `AddObjectWithoutManipulationWithCollision` inside a temporary queue. Public wrappers are `GetValidCandidate(PotentialCandidate, LogFile)` and `EvaluateCandidates(PotentialCandidateSet, LogFile, bool addNullIfInvalid = false)`.

Data carriers:
- `PotentialCandidate` (`PotentialCandidate.cs`) has public fields `database`, `BuildingBlockInstanceData instance`, **`readonly BuildingBlockConfig config`**, `Vector2 center`, `float rotation`, `SeedRandom rand`, `Rect2D bounds`, `string specificVariantId`, ... Its ctor is `(DatabaseConnection, SeedRandom rand, BuildingBlockInstanceData originalTemplate, Vector2 center, float rotation, string origin, bool ignorePersonalSpace = false)`.
- `PlaceCandidate` has `potentialCandidate`, private `surface`, private `template`, and `GeneratedObjectCandidate GetGeneratedObject()`.
- `GeneratedObjectCandidate` has public fields `PlaceableObjectSurface surface`, **`BuildingBlockInstanceDataTemplate template`**, `Rect2D? personalSpaceBounds2D`, `ClipToGrid clipToGrid`, and `string blockId => template.source.blockId`.
- `GeneratedObjectData` has `public List<GeneratedObjectCandidate> objects`.
- `BuildingBlockInstanceDataTemplate` has **`public BuildingBlockInstanceData source`** (settable), `randSeed`, `variantSetId {get; private set}`, `variantIndex {get;set}`, and ctor `(BuildingBlockInstanceData source, string variantSetId, int variantIndex)`.
- `BuildingBlockInstanceData` (`DungeonAlchemist/BuildingBlockInstanceData.cs`) is immutable-style: `blockId {get; private set}`, **`UpdateBlockId(string)`** (`:722`), `UpdateVariant(string variantSetId, string variantId)` (`:1052`), `UpdateScale(db, float, bool overrideScaleLimits=false, float decalDepth=1f)`, `static CreateObject(BuildingBlockConfig, Vector2)`, and `GetBuildingBlockConfig(db)`.

### Recommended interception (generator swap)

**Option A (MVP).** Prefix `MapManipulatorRoomGenratorTarget.SpawnObjects(GeneratedObjectData generatedObjects)` (`DungeonAlchemist.Data/MapManipulatorRoomGenratorTarget.cs:60`) and return `false`. Reimplement its 8-line loop with this per-object logic:
1. Roll the chance, then pick a Workshop config `W` for `template.source.blockId`.
2. Create `swapped = new BuildingBlockInstanceDataTemplate(src.UpdateBlockId(W.Id), null, -1) { randSeed = t.randSeed, allowFloatOnWater = t.allowFloatOnWater }`. Drop the variant set because native variant sets do not apply to Workshop blocks.
3. Call `mapManipulator.objectManipulator.AddObjectWithoutManipulationWithCollision(surface, swapped, clipToGrid)`.
4. If the result is not `PlaceObjectOperationResult.Success`, retry with the original native template.

Private fields are `database`, `mapManipulator`, and `foundation`; read them with `AccessTools.FieldRefAccess`. This point runs after `TemporaryActionQueue.Stop()`, so only real placements are affected, collisions are checked, and the generator's internal state is untouched.

**Option B (more accurate fit, later).** Prefix `PotentialCandidateEvaluator.GetPlaceCandidate`. Swap `potentialCandidate.instance` and the readonly `config` (set it with `AccessTools.Field(...).SetValue`; this works on Mono). Do it **after** scoring and before the collision check, then restore and re-run the original if `__result == null`. This is called for many rejected candidates, and it runs inside the gen thread's simulation.

Side effects to accept: `spawnAtMostOne` and `IsValidOccurenceToSpawn` compare `blockOrCategoryId`, and the neighbourhood score uses `blockId`. Option A avoids both because the generator only ever sees native IDs.

## 4. Themes and placement groups (not block pickers)

- `public class ThemeConfig : PersistentIdDatabaseRecord` (`DungeonAlchemist.Config/ThemeConfig.cs`) has `name`, `icon`, `List<RoomConfig> roomConfigs`, and `GetValidRoomConfigs(RoomInstanceType)`. It holds no block lists.
- `public class PrefabThemeConfig : MonoBehaviour` (`DungeonAlchemist/PrefabThemeConfig.cs`) is the root asset. It holds `themeConfigs[]`, `mainDisplayCategories[]`, `typeConfigs[]`, `brushDisplayCategoryConfigs[]`, and so on. It is passed to `new DatabaseConnection(PrefabThemeConfig, ...)`.
- `PlacementSetGrouper<T>.PlacementGroup { string id; List<T> placements; }` (`RoomGeneration/PlacementSetGrouper.cs`). `RoomPlacementSetGrouper<T>.PickPlacementGroup(SeedRandom rand, IEnumerable<T> placements, LogFile log = null)` groups the example placements by source example room. It is used only by `LinePatternSpawner` and `StairsSpawner`, and it picks *patterns*, not configs.
- The real block source is `ObjectOccurenceData` (`DungeonAlchemist.Analysis/ObjectOccurenceData.cs`): `BlockOrCategoryId blockOrCategoryId`, `List<BuildingBlockVariantId> variants`, `wallPlacements`/`freePlacements` (each with `.template` instance data), `freePatterns`, `linePatterns`, `objectRelationships`, and so on.
- `struct BlockOrCategoryId` wraps a string. `IsCategory(db)`, `Contains(db, blockId)`, and `GetName(db)` are available.
- The **fallback** from the plan's risk section is to mutate occurrence templates or variants before spawning. That is not recommended, because the analysis is cached and shared across rooms in `database.Analyses`.

## 5. BuildingBlockConfig (`DungeonAlchemist.Config/BuildingBlockConfig.cs`)

`public class BuildingBlockConfig : DatabaseRecord`. `DatabaseRecord` is `public string Id { get; set; }`.

- Fields: `string name`, `List<string> keywords`, `BuildingBlockType type` (`Object` is the only valid type for object spawning; `AddObjectWithoutManipulation*` throws otherwise), `string modelId`, `[JsonProperty] public List<string> displayCategoryIds`, `string categoryId` (the AI category), `bool deprecated`, `AutomaticBuildingBlockType automaticType`, **`WallMountType wallMountType`** (`No`, `ForceToWall`, `ClipToWall`), `bool wallMountOnBothSides`, `float scale`, `float xOffset/yOffset/zOffset`, `float rotation`, `ManualColliderSet manualColliderSet`, `Vector2 size`, `bool allowObjectOnObject`, `bool randomizeRotation/randomizeScale`, `float minRandomScale/maxRandomScale`, `DragSpawnType dragSpawnType`, `string customObjectSpawner`.
- Categories: `IEnumerable<string> GetDisplayCategories(db)` and **`GetNormalDisplayCategories(db)`**. The latter filters to `SpecialDisplayCategory.Normal`, which already excludes the "Workshop items" catch-all (`SpecialDisplayCategory { Normal, NewObjects, Favorites, HeroForge, FreeStandingWalls, WorkshopItems, NotSelected, BiteTheBullet, AbstractLight }`). Raw access is `GetDisplayCategoriesIncludingPossiblyInvalid()`.
- AI category: `BlockOrCategoryId GetAICategoryId(db)` returns the category id when `categoryId` is set and that category is `identicalForAI`, otherwise its own `Id`. Related: `HasAICategory(db)`, `GetSimilarCategory(db)`.
- Bounds (all cached, model-space after `GetMeshToModelMatrix()`):
  - `Cube3D GetModelBounds(db)`
  - **`Rect2D GetModelbounds2D(db)`** (lower-case b), which uses `.size.x/.size.y` for the footprint
  - `Rect2D GetCollisionBounds2D(db, int animationState)` (pass `0`)
  - `Cube3D GetCollisionBounds(db, int animationState)`
  - `Rect2D? GetPersonalSpaceCollisionBounds2D(db, int)`
  - `AxisAlignedRect2D GetTileCoverageInModelSpace(db)`
- Raw static data: `database.ModelStaticDataTable.Get(modelId)` returns `ModelSourceStaticData` (`Bounds[] colliderBounds`, `GetStateIndependentMeshBounds()`).
- Workshop checks: `bool IsWorkshopModel(db)` returns `db.GetModelOrigin(modelId) == CustomModelOrigin.Workshop`. **Caveat:** `ImportedModel.AddCustomModel` sets origin `Workshop` for *all* imported models, including local imports.
- Misc: `bool IsInLightCategory(db)`, `bool IsToken()`, `bool IsDecal()`, `bool IsOverlay()`.

## 6. Imported / Workshop config loading

- `DatabaseConnection` (`DungeonAlchemist.Database/DatabaseConnection.cs`):
  - `public Dictionary<string, List<string>> NonDeprecatedBuildingBlockConfigsPerImportedModel { get; private set; }` maps importedModelId (folder GUID) to non-deprecated block ids (`:121`).
  - `public bool IsImportedModelConfig(string blockId)` (`:328`) is backed by private `importedModelConfigInfo` (Dictionary blockId -> `ImportedModelConfigInfo { importedModelId, configDescription, hidden }`).
  - `public string GetImportedModelId(string blockId)`, `public ImportedModelDescription GetImportedModelDescription(string blockId)` (`{ bool local; ulong publishedWorkshopId; string name }`), `public bool IsBlockVisible(string blockId)` (false once unsubscribed or hidden).
  - `public void AddImportedModelDescription(string importedModelId, string blockId, ImportedModelDescription description)` (`:310`) is **sync and called once per registered imported config**. It is the best "config registered" postfix.
  - `public async UniTask AddCustomModelAndUpdateStaticData(BuildingBlockConfig config, CustomModelSource modelSource, CancellationToken)` (`:252`) adds to `ModelTable`, `ModelStaticDataTable`, and `BuildingBlockConfigs.AddCustomModelConfig(config, this)`.
  - `public void HideImportedModel(string importedModelId)` and `public void HideCustomModel(BuildingBlockConfig)` handle removals.
  - `public async UniTask LoadAsync()` (`:192`) loads already-known `ImportedModelFolders` at startup (calls `ImportedModel.AddCustomModel` for each), then `Analyses = new AnalysisTable(...)`. It is called from `DatabaseLoader.cs:56`.
- `ImportedModel` (`DungeonAlchemist.Backend/ImportedModel.cs`):
  - Fields: `fileName`, `Vector3 nativeOffset`, `float nativeScale`, `ModelSourceStaticData staticData`, `List<BuildingBlockConfig> configs`, `string Id => folder.Id`, `ImportedModelFolder Folder`.
  - `public async UniTask AddCustomModel(DatabaseConnection, CancellationToken)` (`:117`). For each config it calls `AddCustomModelAndUpdateStaticData` and `SetCustomModelOrigin(Workshop)`, then fills `NonDeprecatedBuildingBlockConfigsPerImportedModel[Id]` and calls `AddImportedModelDescription`.
- `WorkshopModelLoader` (`DungeonAlchemist.MapEditing.SteamWorkshop/WorkshopModelLoader.cs`):
  - ctor `(DatabaseConnection, SteamWorkshopManager)`, `public void UpdateModels()`, `public bool IsUpdating()`.
  - `private async UniTask UpdateModelsWithCancelToken(CancellationToken)` pages your own and subscribed assets, then calls `public async UniTask AddImportedModel(StopwatchProfiler, string workshopPath, SubscribedAssetInfo asset)` -> `ImportedModelFolder.LoadImportedModel()` -> `ImportedModel.AddCustomModel`.
  - Owned by private field `CustomModelLoader.workshopModelLoader`. `CustomModelLoader.LoadAsync()` (`DungeonAlchemist.Backend/CustomModelLoader.cs:53`) calls `UpdateModels()` **fire-and-forget** after startup. It also runs on `onSubscriptionsChanged`, `CustomModelLoader.UpdateWorkshopAssets()`, `UpdateWorkshopAsset(...)`, and `SubscribeAndDownloadAsset(...)`.
- `LoadImportedModels` is only `private void SelectAssetMenu.LoadImportedModels()` (`DungeonAlchemist.MapEditing/SelectAssetMenu.cs:190`), the asset-editor UI list. It is **not** a loader hook.
- Block table: `BuildingBlockTable` (`DungeonAlchemist.Database/BuildingBlockTable.cs`) has `Get(id)`, `GetOrNull(id)`, `Has(id)`, `ListNonDeprecated()`, `GetAllNonAutomaticBlocks()`, and `AddCustomModelConfig(config, db)`. It is accessed via `database.BuildingBlockConfigs`.
- Display categories: `database.DisplayCategories.Get(id)` returns a `DisplayCategoryConfig` with `.specialCategory`.

**Index-building guidance:**
- Postfix `AddImportedModelDescription`, `HideImportedModel`, and `HideCustomModel` to set a dirty flag.
- Rebuild on the **main thread**, debounced, from a `MonoBehaviour.Update` in the plugin. Workshop loading is async UniTask on the main thread and continues after the menu is up.
- Publish the result as an immutable snapshot through a `volatile` reference swap, and only read that snapshot from the generation thread. Do not enumerate `NonDeprecatedBuildingBlockConfigsPerImportedModel` from the gen thread.
- Filter candidates with `IsBlockVisible`, `!deprecated`, `type == Object`, and `automaticType == None`.

## 7. Brushes (multi-asset paint worker)

- Selection UI: `CategoryPlaceObjectTab` (`DungeonAlchemist.MapEditing/CategoryPlaceObjectTab.cs`).
  - `protected void UpdateBrush()` (`:240`) does `brush = db.TypeConfigs.Get(block.type).CreateBrush(block, this, currentGlobalScale); brush.Create(selectedBlock, OnObjectPlaced);`.
  - `public void PlaceBlock(BuildingBlockVariant)`, `selectedBlock` (property over `blockSelector.selectedBlock`).
  - The brush factory is `ObjectBuildingBlockTypeConfig.CreateBrush(BuildingBlockConfig, AutoInit spawner, float globalScale = 1f) : IPlaceBrush`.
- `public class PlaceObjectBrush : RotatableBrush, IConditionalMouseClickListener<PlaceObjectBrushMode>, IPlaceBrush, IBrush` (`PlaceObjectBrush.cs`):
  - `public void Create(BuildingBlockVariant block, OnObjectPlacedDelegate onDone)` (`:89`), `CreateCloneOf(BuildingBlockInstanceData, OnObjectPlacedDelegate)`, and private `Create(BuildingBlockInstanceData, onDone, string variantSetId, int variantIndex, bool copyModifiersFromVariant, PlaceObjectBrushRotateState)`.
  - State is private `BuildingBlockInstanceDataTemplate template`; the current block is `template.source.blockId`.
  - `public void OnClick(Ray, PlaceObjectBrushMode)` (`:249`). In `Multiple` mode (shift-click), after a successful place it calls `SwapVariant()` and re-randomizes `template.source`. **That is the natural "next stamp" point:** postfix `SwapVariant()` or `OnClick` and set `template.source = template.source.UpdateBlockId(nextId)` plus `UpdateVariant(null,null)`.
  - `public void SpawnDragSurfaces()` (`:297`) handles drag-line and area spawn with one `template` for all surfaces. A per-surface swap needs a prefix that replaces the loop.
  - `private void UpdatePreview()` re-reads `template.source.blockId` every frame, so the preview follows swaps automatically.
  - `public void SwapVariant()`, `public void DragArea(Ray, BuildingBlockConfig)`.
- `public class DrawObjectsBrush : MapEditorAutoInit, IMouseDragListener, ISplatObjectDensityBrush, ...` (`DrawObjectsBrush.cs`):
  - `public void Create(ObjectBrushConfig config, float brushLevel, float densityLevel, BrushType type, float brushRotationOffset = 1f, float brushPositionOffset = 1f)` builds one `DrawObjectsSpawner` per `config.families`.
  - Created from `PaintObjectsTab.cs:274,487` (and `PaintEffectsTab`).
  - `DrawObjectsBrushSpawnMode { InRoom, InExterior }` is set in `OnStartDrag`.
- `public class ObjectBrushConfig : PersistentIdDatabaseRecord` has `name`, `List<ObjectBrushFamily> families`, `overrideDefaultPresets`, `brushTypeOverride`, and the randomness overrides.
  - `ObjectBrushFamily { TerrainSpawnObjectFamily family; float spawnsPerSquareMeter, minDistanceBetweenObjects, minBrushSizeScale, maxBrushSizeScale; }`.
  - `TerrainSpawnObjectFamily : PersistentIdMonoBehaviour { List<TerrainSpawnBuildingBlockReference> blocks; float minScale, maxScale; bool spawnOnLand, spawnOnWater; ... }`.
  - `TerrainSpawnBuildingBlockReference : BuildingBlockReference { float spawnRate; }` (block id in `.value`).
- `DrawObjectsSpawner` (`DrawObjectsSpawner.cs`):
  - The ctor caches `validBlocks = family.family.blocks.Where(Has)`.
  - `private bool AttemptObjectSpawn(bool placeUndoCheckpoint, Vector2 spawnPos, float optionalAngle = 0f)` picks with `RandomUtility.PickWeighted(validBlocks, spawnRate)`, then `private AddObjectOperation AddObject(BuildingBlockConfig, PlaceableObjectSurface, bool, float)` -> `MapEditor.AddBrushObject(TerrainSpawnObjectFamily, PlaceableObjectSurface, BuildingBlockInstanceDataTemplate, float rotation, bool placeUndoCheckpoint = true)` (`MapEditor.cs:523`).
  - **The paint brush is already multi-asset.** A synthetic `ObjectBrushConfig` with a runtime `TerrainSpawnObjectFamily` (a MonoBehaviour, so create it on a hidden GameObject) whose `blocks` are the user's selected Workshop ids gives scatter painting with no patches to the spawn logic.
- `public class BuildingBlockSelector : BuildingBlockAnimatedSelectorGrid` (`BuildingBlockSelector.cs`):
  - `BuildingBlockVariant selectedBlock { get; set; }` is single-select through `base.selectedEntry`.
  - `Create(DatabaseConnection, GetDisplayListDelegate, ObjectPreviewRenderer, Action onChange, bool hasFavoriteButton)`, `GenerateSearch()`.
  - Supports the `BLOCKID:<id>` search syntax.
- `public class MultiSelector : AutoInit` (`DungeonAlchemist.Forms/MultiSelector.cs`): `Create(List<MultiSelectorEntry> allEntries, List<string> selectedEntries, MultiSelectorSaveDelegate onSave, string titleText)` uses string ids and a save callback. It is a reusable prefab-based picker.
- `BuildingBlockVariant { BuildingBlockConfig block; string variantId; int variantIndex; }` (`DungeonAlchemist.Config/BuildingBlockVariant.cs`).
