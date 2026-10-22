using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Settings.Tools;
using HarmonyLib;
using Keen.Game2.Client.GameSystems.BlockPlacement.BlockPlacer;
using Keen.Game2.Client.GameSystems.PlayerControl;
using Keen.Game2.Simulation.GameSystems.Progression;
using Keen.Game2.Client.UI.HUD.Toolbar.ToolbarState;
using Keen.Game2.Client.UI.InGame;
using Keen.Game2.Client.WorldObjects.Shared;
using Keen.Game2.Client.UI.Shared.SystemNotification;
using Keen.Game2.Simulation.GameSystems.BlockPlacement;
using Keen.Game2.Simulation.GameSystems.BlockPlacement.Projections;
using Keen.VRage.Library.Mathematics;
using Keen.Game2.Simulation.GameSystems.Toolbars;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Core.Input;
using Keen.VRage.DCS.Components;
using Keen.VRage.DCS.Definitions;
using Keen.VRage.Input;
using Keen.VRage.Library.Diagnostics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Library.Threading;
using Keen.VRage.Multiplayer.Sessions;
using Keen.Game2.Simulation.WorldObjects.Tools;
using Keen.VRage.DCS.ObjectBuilders;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Library.Definitions;
using Keen.VRage.Library.Extensions;
using Keen.VRage.Library.Collections;

namespace ClientPlugin.Patches;

// Block picker: while a block is in hand and you aim at a block (built or projected),
// pressing the keybind picks that block: selects it on the build bar (adding it to a free slot if
// needed), optionally picks its paint color, or holds it without touching the build bar.
// Key polling uses a Session.Update postfix, like the template's keybind example.
[HarmonyPatch(typeof(Session))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class BlockPickerPatch
{
    private static bool wasPressed;

    // Reflection results are looked up once, not on every key press.
    private static readonly FieldInfo PaintDataField = AccessTools.Field(typeof(BlockPlacerEntityComponent), "_paintData");
    private static readonly FieldInfo PlacerDataField = AccessTools.Field(typeof(BlockPlacerEntityComponent), "_placerData");
    private static readonly FieldInfo[] ColorFloatFields = typeof(ColorHSV)
        .GetFields(BindingFlags.Public | BindingFlags.Instance).Where(x => x.FieldType == typeof(float)).ToArray();
    private static readonly PropertyInfo[] ColorFloatProps = typeof(ColorHSV)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(x => x.PropertyType == typeof(float) && x.GetIndexParameters().Length == 0).ToArray();

    // Aimed block definition -> build bar version of that block (found once, then reused).
    private static readonly Dictionary<object, EntityCompositeDefinition> CompositeCache =
        new Dictionary<object, EntityCompositeDefinition>(ReferenceEqualityComparer.Instance);
    private static Type blockKindType;
    private static bool compositeSearchLogged;

    // Selection waiting for the server to deliver a newly added tile.
    private static ToolbarSelection? pendingSlot;
    private static EntityCompositeDefinition pendingComposite;
    private static int pendingFrames;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Session.Update), typeof(bool))]
    public static void UpdatePostfix(Session __instance)
    {
        try
        {
            var inGameUI = __instance.TryGet<SessionInGameUISessionComponent>();
            if (inGameUI == null)
                return; // server side session

            var config = Config.Current;
            if (!config.Enabled)
                return;

            ProcessPendingSelection(__instance);

            var binding = config.PickKey;
            if (!binding.IsBound)
            {
                wasPressed = false;
                return;
            }

            var keyboard = __instance.TryGet<IInputManager>()?.Keyboard;
            if (keyboard == null)
                return;

            var pressed = IsComboDown(keyboard, binding);

            if (pressed && !wasPressed)
                PickAimedBlock(__instance, inGameUI, config);

            wasPressed = pressed;
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] UpdatePostfix failed: {e}");
        }
    }

    // Puts the block in the player's hand through a temporary tile that is not on the build bar.
    private static void EquipDirectly(Session session, SessionInGameUISessionComponent ui, Config config,
        BlockPlacerEntityComponent placer, BlockPlacementTarget aimed, CubeBlockDefinition wanted)
    {
        try
        {
            var composite = FindComposite(wanted);
            if (composite == null)
            {
                Notify(ui, config, "Could not find that block");
                return;
            }

            if (!IsUnlocked(session, composite))
            {
                Notify(ui, config, "That block is not unlocked yet");
                return;
            }

            // Color first, so the block preview already uses it.
            if (config.PickColor)
                ApplyColor(session, placer, aimed, ui, config);

            var tile = new BlockListToolbarTile
            {
                Icon = wanted.UIData.Icon,
                Blocks = new List<EntityCompositeDefinition> { composite },
            };
            tile.LastSelectedBlock = composite;
            placer.HandleBlockToolbarTile(tile);

            Log.Default.WriteLine($"[{Plugin.Name}] Equipped block without using the build bar");
            Notify(ui, config, "Block picked");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] EquipDirectly failed: {e}");
        }
    }

    // Selects the palette entry matching the aimed block's paint color.
    private static void ApplyColor(Session session, BlockPlacerEntityComponent placer, BlockPlacementTarget aimed, SessionInGameUISessionComponent ui, Config config)
    {
        try
        {
            ColorHSV? color = null;
            if (aimed is CubeBlockPlacementTarget cube)
                color = cube.Block.Color;
            else if (aimed is ProjectionBlockPlacementTarget projection)
                color = projection.Source.TryGetOB(projection.TargetBlockRef).OB<CubeBlockComponent, CubeBlockObjectBuilder>().Color;

            if (!color.HasValue)
                return;

            var paintData = PaintDataField?.GetValue(placer) as PaintToolPerPlayerData;
            var placerData = PlacerDataField?.GetValue(placer) as BlockPlacerPerPlayerData;
            if (paintData == null || placerData == null)
            {
                Log.Default.WriteLine($"[{Plugin.Name}] Paint data not found");
                return;
            }

            for (var i = 0; i < paintData.Palette.Count; i++)
            {
                if (ColorsClose(paintData.Palette[i], color.Value))
                {
                    placerData.PaletteIndex = i;
                    Log.Default.WriteLine($"[{Plugin.Name}] Selected palette color {i}");
                    return;
                }
            }

            if (config.AddMissingColor)
            {
                // Same request the paint tool uses: the server replaces the palette entry at that index.
                PaintToolPerPlayerData.SetPaintColor(session, color.Value, placerData.PaletteIndex).SkipWait();
                Log.Default.WriteLine($"[{Plugin.Name}] Put block color into palette slot {placerData.PaletteIndex}");
                Notify(ui, config, "Color added to your palette");
                return;
            }

            Log.Default.WriteLine($"[{Plugin.Name}] Block color is not in the palette: {color.Value}");
            Notify(ui, config, "That block's color is not in your palette");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] ApplyColor failed: {e}");
        }
    }

    // Colors count as equal when all their numeric parts are within a tiny tolerance.
    private static bool ColorsClose(ColorHSV a, ColorHSV b)
    {
        if (a.Equals(b))
            return true;

        var any = false;
        foreach (var field in ColorFloatFields)
        {
            any = true;
            if (Math.Abs((float)field.GetValue(a) - (float)field.GetValue(b)) > 0.01f)
                return false;
        }

        foreach (var prop in ColorFloatProps)
        {
            any = true;
            if (Math.Abs((float)prop.GetValue(a) - (float)prop.GetValue(b)) > 0.01f)
                return false;
        }

        return any;
    }

    // Same block definition? Reference first; projections may carry another instance of the same definition.
    private static bool SameBlock(CubeBlockDefinition a, CubeBlockDefinition b)
    {
        if (a == null || b == null)
            return false;
        if (ReferenceEquals(a, b))
            return true;
        try
        {
            return a.LocalizableBlockTypeDisplayName.EvaluateText() == b.LocalizableBlockTypeDisplayName.EvaluateText()
                   && a.BoundingBox.Equals(b.BoundingBox);
        }
        catch
        {
            return false;
        }
    }

    // Survival progression: only blocks the player has unlocked may be picked or added.
    private static bool IsUnlocked(Session session, EntityCompositeDefinition block)
    {
        try
        {
            var progression = session.TryGet<ProgressionSessionComponent>();
            var players = session.TryGet<ClientPlayersSessionComponent>();
            if (progression == null || players == null)
                return true; // cannot check, do not block
            return progression.IsBlockUnlocked(block, players.LocalPlayerIdentity);
        }
        catch (Exception e)
        {
            Log.Default.WriteLine($"[{Plugin.Name}] Unlock check failed: {e.Message}");
            return true;
        }
    }

    private static void ProcessPendingSelection(Session session)
    {
        if (!pendingSlot.HasValue)
            return;

        var toolbar = session.TryGet<PlayerToolbarStateSessionComponent>();
        var slot = pendingSlot.Value;
        if (toolbar != null &&
            slot.ToolbarIndexIndex < toolbar.Tiles.Count &&
            slot.TileIndex < toolbar.Tiles[slot.ToolbarIndexIndex].Count &&
            toolbar.Tiles[slot.ToolbarIndexIndex][slot.TileIndex] is BlockToolbarTile placed)
        {
            placed.LastSelectedBlock = pendingComposite;
            toolbar.Selection = slot;
            pendingSlot = null;
            pendingComposite = null;
            return;
        }

        if (++pendingFrames > 300) // give up after a few seconds
        {
            pendingSlot = null;
            pendingComposite = null;
        }
    }

    public static bool IsComboDown(IInputDevice keyboard, Binding binding)
    {
        var mainKey = new DigitalInput(binding.Vk, GenericDeviceClass.Keyboard);
        return mainKey.IsActive(keyboard) &&
               KeyboardInputs.Control.IsActive(keyboard) == binding.Ctrl &&
               KeyboardInputs.Alt.IsActive(keyboard) == binding.Alt &&
               KeyboardInputs.Shift.IsActive(keyboard) == binding.Shift;
    }

    private static void PickAimedBlock(Session session, SessionInGameUISessionComponent ui, Config config)
    {
        // Find the block placer that is currently active (a block is in hand).
        BlockPlacerEntityComponent placer = null;
        foreach (var entity in session.GetEntitiesOfType<BlockPlacerEntityComponent>())
        {
            var candidate = entity.Get<BlockPlacerEntityComponent>();
            if (candidate != null && candidate.IsActive())
            {
                placer = candidate;
                break;
            }
        }

        if (placer == null)
        {
            Log.Default.WriteLine($"[{Plugin.Name}] No active block placer (no block in hand?)");
            return;
        }

        var aimed = placer.AlignedBlock.Target;
        if (aimed == null)
        {
            Log.Default.WriteLine($"[{Plugin.Name}] Not aiming at a block");
            return;
        }

        var wanted = aimed.Definition;

        if (config.EquipWithoutToolbar)
        {
            EquipDirectly(session, ui, config, placer, aimed, wanted);
            return;
        }

        var toolbar = session.TryGet<PlayerToolbarStateSessionComponent>();
        if (toolbar == null)
        {
            Log.Default.WriteLine($"[{Plugin.Name}] Toolbar state not available");
            return;
        }

        for (var row = 0; row < toolbar.Tiles.Count; row++)
        {
            var tiles = toolbar.Tiles[row];
            for (var col = 0; col < tiles.Count; col++)
            {
                if (!(tiles[col] is BlockToolbarTile tile))
                    continue;

                foreach (var block in tile.GetAllBlocks())
                {
                    if (!SameBlock(block.Definition<CubeBlockComponent, CubeBlockDefinition>(), wanted))
                        continue;

                    if (!IsUnlocked(session, block))
                    {
                        Notify(ui, config, "That block is not unlocked yet");
                        return;
                    }

                    // Color first, so the block preview already uses it.
                    if (config.PickColor)
                        ApplyColor(session, placer, aimed, ui, config);

                    // Remember the exact variant so the placer equips it.
                    tile.LastSelectedBlock = block;

                    var slot = new ToolbarSelection(row, col);
                    if (toolbar.Selection == slot)
                        placer.HandleBlockToolbarTile(tile); // already on this slot, force a reload
                    else
                        toolbar.Selection = slot;

                    Log.Default.WriteLine($"[{Plugin.Name}] Picked block on toolbar {row} slot {col}");
                    Notify(ui, config, "Block picked");
                    return;
                }
            }
        }

        Log.Default.WriteLine($"[{Plugin.Name}] Aimed block is not on any toolbar slot, trying to add it");
        TryAddToToolbar(session, ui, config, placer, toolbar, aimed, wanted);
    }

    private static async void TryAddToToolbar(Session session, SessionInGameUISessionComponent ui, Config config,
        BlockPlacerEntityComponent placer, PlayerToolbarStateSessionComponent toolbar,
        BlockPlacementTarget aimed, CubeBlockDefinition wanted)
    {
        try
        {
            var composite = FindComposite(wanted);
            if (composite == null)
            {
                Notify(ui, config, "That block is not on your build bar (could not add it)");
                return;
            }

            if (!IsUnlocked(session, composite))
            {
                Notify(ui, config, "That block is not unlocked yet");
                return;
            }

            // First free slot, tab by tab. The client may not edit the toolbar directly,
            // so the change is requested from the server (same as the G screen does).
            ToolbarSelection? free = null;
            for (var row = 0; row < toolbar.Tiles.Count && !free.HasValue; row++)
            {
                var tiles = toolbar.Tiles[row];
                for (var col = 0; col < tiles.Count; col++)
                {
                    if (tiles[col] == null || tiles[col] is EmptyToolbarTile)
                    {
                        free = new ToolbarSelection(row, col);
                        break;
                    }
                }
            }

            if (!free.HasValue)
            {
                Notify(ui, config, "Your build bar is full");
                return;
            }

            if (config.PickColor)
                ApplyColor(session, placer, aimed, ui, config);

            var slot = free.Value;
            var newTile = new BlockListToolbarTile
            {
                Icon = wanted.UIData.Icon,
                Blocks = new List<EntityCompositeDefinition> { composite },
            };

            var released = session.Get<IObservableReplication>().Release<ToolbarTile>(newTile);
            await PlayerToolbarServerSessionComponent.RequestSetTile(session, slot, released);

            // The new tile reaches the client a moment later: select it from the frame update (see below).
            pendingSlot = slot;
            pendingComposite = composite;
            pendingFrames = 0;

            Log.Default.WriteLine($"[{Plugin.Name}] Added block to toolbar {slot.ToolbarIndexIndex} slot {slot.TileIndex}");
            Notify(ui, config, "Block added to your build bar");
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] TryAddToToolbar failed: {e}");
        }
    }

    // Finds the build-bar version (server-side composite) of a placed block.
    // The G screen lists blocks through BlockKindDefinition.Blocks, so search those for a block
    // that uses the same CubeBlockDefinition as the aimed block. Results are cached.
    private static EntityCompositeDefinition FindComposite(CubeBlockDefinition wanted)
    {
        if (CompositeCache.TryGetValue(wanted, out var cached))
            return cached;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            blockKindType ??= AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(x => x.Name == "BlockKindDefinition");
            if (blockKindType == null)
            {
                Log.Default.WriteLine($"[{Plugin.Name}] BlockKindDefinition type not found");
                return null;
            }

            var manager = Singleton<DefinitionManager>.Instance;
            var managerType = manager.GetType();
            foreach (var m in managerType.GetMethods(flags))
            {
                if (!m.Name.Contains("Definitions") && !m.Name.Contains("GetAll"))
                    continue;

                IEnumerable list = null;
                try
                {
                    var ps = m.GetParameters();
                    if (m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && ps.Length == 0)
                        list = m.MakeGenericMethod(blockKindType).Invoke(manager, null) as IEnumerable;
                    else if (!m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType == typeof(Type))
                        list = m.Invoke(manager, new object[] { blockKindType }) as IEnumerable;
                }
                catch (Exception e)
                {
                    Log.Default.WriteLine($"[{Plugin.Name}] {m.Name} failed: {e.Message}");
                }

                if (list == null)
                    continue;

                foreach (var kind in list)
                {
                    var blocksProp = kind?.GetType().GetProperty("Blocks", flags);
                    if (!(blocksProp?.GetValue(kind) is IEnumerable blocks))
                        continue;
                    foreach (var item in blocks)
                    {
                        if (item is EntityCompositeDefinition c &&
                            SameBlock(c.Definition<CubeBlockComponent, CubeBlockDefinition>(), wanted))
                        {
                            CompositeCache[wanted] = c;
                            return c;
                        }
                    }
                }
            }

            if (!compositeSearchLogged)
            {
                compositeSearchLogged = true;
                Log.Default.WriteLine($"[{Plugin.Name}] Composite not found. DefinitionManager methods: " +
                    string.Join(" | ", managerType.GetMethods(flags).Select(x => x.ToString()).Distinct()));
            }
        }
        catch (Exception e)
        {
            Log.Default.WriteLine($"[{Plugin.Name}] Composite search failed: {e}");
        }

        return null;
    }

    private static void Notify(SessionInGameUISessionComponent ui, Config config, string text)
    {
        if (!config.ShowMessages)
            return;

        ui.DisplayNotification(new NotificationViewModel(
            title: Plugin.Name,
            content: text,
            timeout: TimeSpan.FromSeconds(2)));
    }
}

// The game opens the G / build screen when G is pressed, even with Ctrl held.
// While our pick combo is held, skip the game's "toggle screen" action.
[HarmonyPatch(typeof(PlayerKeybindsInputHandlerComponent), "ToggleScreen")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
public static class BlockPickerSuppressGMenuPatch
{
    [HarmonyPrefix]
    public static bool Prefix(PlayerKeybindsInputHandlerComponent __instance)
    {
        try
        {
            var config = Config.Current;
            if (!config.Enabled || !config.PickKey.IsBound)
                return true;

            var keyboard = __instance.GetSession().TryGet<IInputManager>()?.Keyboard;
            if (keyboard == null)
                return true;

            if (BlockPickerPatch.IsComboDown(keyboard, config.PickKey))
                return false; // our combo is held: don't open the screen
        }
        catch (Exception e)
        {
            Log.Default.WriteLine(LogSeverity.Error, $"[{Plugin.Name}] Suppress patch failed: {e}");
        }
        return true;
    }
}
