﻿using Archipelago.HollowKnight.IC;
using Archipelago.HollowKnight.MC;
using Archipelago.HollowKnight.SlotData;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Exceptions;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using ItemChanger;
using ItemChanger.Internal;
using ItemChanger.Tags;
using Modding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Archipelago.HollowKnight
{
    public class Archipelago : Mod, IGlobalSettings<ConnectionDetails>, ILocalSettings<ConnectionDetails>
    {
        // Events support
        public static event Action OnArchipelagoGameStarted;
        public static event Action OnArchipelagoGameEnded;

        /// <summary>
        /// Minimum Archipelago Protocol Version
        /// </summary>
        private readonly Version ArchipelagoProtocolVersion = new(0, 3, 3);

        /// <summary>
        /// Mod version as reported to the modding API
        /// </summary>
        public override string GetVersion()
        {
            Version assemblyVersion = GetType().Assembly.GetName().Version;
            string version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
#if DEBUG
            using SHA1 sha = SHA1.Create();
            using FileStream str = File.OpenRead(GetType().Assembly.Location);
            StringBuilder sb = new();
            foreach (byte b in sha.ComputeHash(str).Take(4))
            {
                sb.AppendFormat("{0:x2}", b);
            }
            version += "-prerelease+" + sb.ToString();
#endif
            return version;
        }
        public static Archipelago Instance;
        public ArchipelagoSession session { get; private set; }
        public SlotOptions SlotOptions { get; set; }
        public bool ArchipelagoEnabled { get; set; }

        public int Slot { get; private set; }
        public IReadOnlyDictionary<int, NetworkSlot> AllSlots { get; private set; }
        public string Player => session.Players.GetPlayerName(Slot);

        public bool DeferringLocationChecks { get; private set; }
        public bool ShouldReceiveChecks { get; private set; } = false;
        public Goal Goal { get; private set; } = null;
        public bool GoalIsKnown { get; private set; } = false;  // Not Yet Implemented
        
        // Shade position fixes
        public static readonly Dictionary<string, (float x, float y)> ShadeSpawnPositionFixes = new()
        {
            { "Abyss_08", (90.0f, 90.0f) },  // Lifeblood Core room.  Even outside of deathlink, shades spawn out of bounds.
            { "Room_Colosseum_Spectate", (124.0f, 10.0f) },  // Shade spawns inside inaccessible arena
            { "Resting_Grounds_09", (7.4f, 10.0f) },  // Shade spawns underground.
            { "Runes1_18", (11.5f, 23.0f) },  // Shade potentially spawns on the wrong side of an inaccessible gate.
        };

        // Placements to attempt hinting
        public HashSet<AbstractPlacement> PendingPlacementHints = new();

        internal SpriteManager spriteManager;

        internal ConnectionDetails MenuSettings = new()
        {
            ServerUrl = "archipelago.gg",
            ServerPort = 38281,
        };

        internal ConnectionDetails ApSettings = new();

        /// <summary>
        /// Allows lookup of a placement by its location ID number.  Used during syncing and shared-slot coop.
        /// </summary>
        internal readonly Dictionary<long, AbstractPlacement> placementsByLocationID = new();

        /// <summary>
        /// List of pending locations.
        /// </summary>
        private readonly HashSet<long> deferredLocationChecks = new();
        private int pendingGeo = 0;
        private TimeSpan timeBetweenReceiveItem = TimeSpan.FromMilliseconds(500);
        private DateTime lastUpdate = DateTime.MinValue;

        /// <summary>
        /// A preset GiveInfo structure that avoids creating geo and places messages in the corner.
        /// </summary>
        internal GiveInfo RemoteGiveInfo = new()
        {
            FlingType = FlingType.DirectDeposit,
            Callback = null,
            Container = Container.Unknown,
            MessageType = MessageType.Corner
        };

        /// <summary>
        /// A preset GiveInfo structure that avoids creating geo and outputs no messages, e.g. for Start Items.
        /// </summary>
        internal GiveInfo SilentGiveInfo = new()
        {
            FlingType = FlingType.DirectDeposit,
            Callback = null,
            Container = Container.Unknown,
            MessageType = MessageType.None
        };

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            base.Initialize();
            Log("Initializing");
            Instance = this;
            spriteManager = new SpriteManager(typeof(Archipelago).Assembly, "Archipelago.HollowKnight.Resources.");

            MenuChanger.ModeMenu.AddMode(new ArchipelagoModeMenuConstructor());
            Log("Initialized");
        }

        private void OnSceneEntered(On.GameManager.orig_FinishedEnteringScene orig, GameManager self)
        {
            orig(self);
            if (!ShouldReceiveChecks)
            {
                SynchronizeCheckedLocations();
                StopDeferringLocationChecks();
                if (pendingGeo > 0)
                {
                    HeroController.instance.AddGeo(pendingGeo);
                    pendingGeo = 0;
                }
                ShouldReceiveChecks = true;
            }
        }

        private void SynchronizeCheckedLocations()
        {
            if (ArchipelagoEnabled)
            {
                DeferLocationChecks();
                // Receive items until the queue is empty.
                while (ReceiveNextItem())
                {
                }

                foreach (long location in session.Locations.AllLocationsChecked)
                {
                    MarkLocationAsChecked(location);
                }
            }
        }

        public void DeclareVictory()
        {
            LogDebug($"Declaring victory if ArchipelagEnabled.  ArchipelagoEnabled = {ArchipelagoEnabled}");
            if (ArchipelagoEnabled)
            {
                try
                {
                    session.Socket.SendPacket(new StatusUpdatePacket()
                    {
                        Status = ArchipelagoClientState.ClientGoal
                    });
                }
                catch (ArchipelagoSocketClosedException)
                {
                    ReportDisconnect();
                }
            }
        }

        private bool ReceiveNextItem()
        {
            if (!session.Items.Any())
            {
                return false; // No items are waiting.
            }

            LogDebug($"Item Index from lib is: {session.Items.Index}. From APSettings it is: {ApSettings.ItemIndex}");

            NetworkItem netItem = session.Items.DequeueItem(); // Read the next item
            if (ApSettings.ItemIndex >= session.Items.Index) // We've already handled this, so be done
            {
                return true;
            }

            try
            {
                ReceiveItem(netItem);
            }
            finally
            {
                ApSettings.ItemIndex++;
            }

            return true;
        }

        private void ModHooks_HeroUpdateHook()
        {
            if (!ShouldReceiveChecks)
            {
                return;
            }

            if (DeferringLocationChecks)
            {
                StopDeferringLocationChecks();
            }

            if (DateTime.Now - timeBetweenReceiveItem > lastUpdate && session.Items.Any())
            {
                ReceiveNextItem();
            }
        }

        public void EndGame()
        {
            LogDebug("Ending Archipelago game");
            SendPlacementHints();
            try
            {
                OnArchipelagoGameEnded?.Invoke();
            }
            catch (Exception ex)
            {
                LogError($"Error invoking OnArchipelagoGameEnded:\n {ex}");
            }

            DisconnectArchipelago();
            ArchipelagoEnabled = false;
            ApSettings = new();
            ShouldReceiveChecks = false;

            ItemChanger.Events.OnItemChangerUnhook -= EndGame;
            ItemChanger.Events.OnSceneChange -= Events_OnSceneChange;
            ModHooks.HeroUpdateHook -= ModHooks_HeroUpdateHook;
            ModHooks.AfterPlayerDeadHook -= ModHooks_AfterPlayerDeadHook;
            On.GameManager.FinishedEnteringScene -= OnSceneEntered;

            if (Goal != null)
            {
                Goal.Deselect();
                Goal = null;
            }
        }

        /// <summary>
        /// Call when starting or resuming a game to randomize and restore state.
        /// </summary>
        public void StartOrResumeGame(bool randomize)
        {
            if (!ArchipelagoEnabled)
            {
                LogDebug("StartOrResumeGame: This is not an Archipelago Game, so not doing anything.");
                return;
            }

            LogDebug("StartOrResumeGame: This is an Archipelago Game.");

            LoginSuccessful loginResult = ConnectToArchipelago();
            DeferLocationChecks();
            if (randomize)
            {
                LogDebug("StartOrResumeGame: Beginning first time randomization.");
                ApSettings.ItemIndex = 0;
                ApSettings.Seed = (long) loginResult.SlotData["seed"];
                ApSettings.RoomSeed = session.RoomState.Seed;

                LogDebug($"StartOrResumeGame: Room: {ApSettings.RoomSeed}; Seed = {ApSettings.RoomSeed}");

                ArchipelagoRandomizer randomizer = new(loginResult.SlotData);
                randomizer.Randomize();
                pendingGeo = SlotOptions.StartingGeo;
            }
            else
            {
                LogDebug($"StartOrResumeGame: Local : Room: {ApSettings.RoomSeed}; Seed = {ApSettings.Seed}");
                long seed = (long) loginResult.SlotData["seed"];
                LogDebug($"StartOrResumeGame: AP    : Room: {session.RoomState.Seed}; Seed = {seed}");
                if (seed != ApSettings.Seed || session.RoomState.Seed != ApSettings.RoomSeed)
                {
                    if (ApSettings.RoomSeed == null)
                    {
                        LogWarn(
                            "Are you upgrading from a previous version?  Seed data did not exist in save.  It does now.");
                        ApSettings.Seed = seed;
                        ApSettings.RoomSeed = session.RoomState.Seed;
                    }
                    else
                    {
                        DisconnectArchipelago();
                        UIManager.instance.UIReturnToMainMenu();
                        throw new ArchipelagoConnectionException(
                            "Slot mismatch.  Saved seed does not match the server value.  Is this the correct save?");
                    }
                }
                pendingGeo = 0;
            }

            // Load any existing text plando data saved to slot on either new game or loaded save.
            try
            {
                List<List<string>> textPlando = SlotDataExtract.ExtractArrayFromSlotData<List<List<string>>>(loginResult.SlotData["plando_texts"]);
                foreach (List<string> keySheetText in textPlando)
                {
                    TextChanger.TextChanger.LoadedInstance.addOverride(keySheetText[0], keySheetText[1], keySheetText[2]);
                }
            }
            catch
            {
                //plando_texts not present in game
            }

            // Hooks happen after we've definitively connected to an Archipelago slot correctly.
            // Doing this before checking for the correct slot/seed/room will cause problems if
            // the client connects to the wrong session with a matching slot.
            ItemChanger.Events.OnItemChangerUnhook += EndGame;
            ItemChanger.Events.OnSceneChange += Events_OnSceneChange;
            ModHooks.HeroUpdateHook += ModHooks_HeroUpdateHook;
            ModHooks.AfterPlayerDeadHook += ModHooks_AfterPlayerDeadHook;
            On.GameManager.FinishedEnteringScene += OnSceneEntered;

            // Discard from the beginning of the incoming item queue up to how many items we have received.
            for (int i = 0; i < ApSettings.ItemIndex; ++i)
            {
                NetworkItem netItem = session.Items.DequeueItem();
                LogDebug($"Fast-forwarding past an already-acquired {session.Items.GetItemName(netItem.Item)}");
            }

            try
            {
                Goal = Goal.GetGoal(SlotOptions.Goal);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                UIManager.instance.UIReturnToMainMenu();
                LogError(
                    $"Listed goal is {SlotOptions.Goal}, which is greater than {GoalsLookup.MAX}.  Is this an outdated client?");
                throw ex;
            }

            Goal.Select();

            try
            {
                OnArchipelagoGameStarted?.Invoke();
            }
            catch (Exception ex)
            {
                LogError($"Error invoking OnArchipelagoGameStarted:\n {ex}");
            }
        }

        private void Events_OnSceneChange(UnityEngine.SceneManagement.Scene obj)
        {
            SendPlacementHints();
        }

        private void Socket_SocketClosed(string reason)
        {
            ReportDisconnect();
        }

        private LoginSuccessful ConnectToArchipelago()
        {
            session = ArchipelagoSessionFactory.CreateSession(ApSettings.ServerUrl, ApSettings.ServerPort);
            session.Socket.PacketReceived += OnPacketReceived;

            LoginResult loginResult = session.TryConnectAndLogin("Hollow Knight",
                                                         ApSettings.SlotName,
                                                         ItemsHandlingFlags.AllItems,
                                                         ArchipelagoProtocolVersion,
                                                         password: ApSettings.ServerPassword);

            if (loginResult is LoginFailure failure)
            {
                string errors = string.Join(", ", failure.Errors);
                LogError($"Unable to connect to Archipelago because: {string.Join(", ", failure.Errors)}");
                throw new ArchipelagoConnectionException(errors);
            }
            else if (loginResult is LoginSuccessful success)
            {
                // Read slot data.
                Slot = success.Slot;
                SlotOptions = SlotDataExtract.ExtractObjectFromSlotData<SlotOptions>(success.SlotData["options"]);
                session.Socket.SocketClosed += Socket_SocketClosed;

                LogDebug($"Deathlink type: {SlotOptions.DeathLink}");
                // Enable Deathlink
                if (SlotOptions.DeathLink != DeathLinkType.None)
                {
                    DeathLinkSupport.Instance.Enable();
                }
                else
                {
                    DeathLinkSupport.Instance.Disable();
                }

                return success;
            }
            else
            {
                LogError($"Unexpected LoginResult type when connecting to Archipelago: {loginResult}");
                throw new ArchipelagoConnectionException("Unexpected login result.");
            }
        }

        private void OnPacketReceived(ArchipelagoPacketBase packet)
        {
            if (packet is ConnectedPacket cp)
            {
                session.Socket.PacketReceived -= OnPacketReceived;
                AllSlots = cp.SlotInfo;
            }
        }

        public void MarkLocationAsChecked(long locationID)
        {
            // Called when marking a location as checked remotely (i.e. through ReceiveItem, etc.)
            // This also grants items at said locations.
            AbstractPlacement pmt;
            bool hadNewlyObtainedItems = false;
            bool hadUnobtainedItems = false;

            LogDebug($"Marking location {locationID} as checked.");
            if (!placementsByLocationID.TryGetValue(locationID, out pmt))
            {
                LogDebug($"Could not find a placement for location {locationID}");
                return;
            }

            foreach (AbstractItem item in pmt.Items)
            {
                if (!item.GetTag(out ArchipelagoItemTag tag))
                {
                    hadUnobtainedItems = true;
                    continue;
                }

                if (item.WasEverObtained())
                {
                    continue;
                }

                if (tag.Location != locationID)
                {
                    hadUnobtainedItems = true;
                    continue;
                }

                hadNewlyObtainedItems = true;
                pmt.AddVisitFlag(VisitState.ObtainedAnyItem);

                item.Give(pmt, RemoteGiveInfo);
            }

            if (hadNewlyObtainedItems && !hadUnobtainedItems)
            {
                pmt.AddVisitFlag(VisitState.Opened | VisitState.Dropped | VisitState.Accepted |
                                 VisitState.ObtainedAnyItem);
            }
        }

        public void ReceiveItem(NetworkItem netItem)
        {
            string name = session.Items.GetItemName(netItem.Item);
            LogDebug(
                $"Receiving item ID {netItem.Item}.  Name is {name}.  Slot is {netItem.Player}.  Location is {netItem.Location}.");

            if (netItem.Player == Slot && netItem.Location > 0)
            {
                MarkLocationAsChecked(netItem.Location);
                return;
            }

            // If we're still here, this is an item from someone else.  We'll make up our own dummy placement and grant the item.
            AbstractItem item = Finder.GetItem(name);
            if (item == null)
            {
                LogDebug(
                    $"Could not find an item named '{name}'.  This means that item {netItem.Item} was not received.");
                return;
            }

            string sender;
            if (netItem.Location == -1)
            {
                sender = "Cheat Console";
            }
            else if (netItem.Location == -2)
            {
                sender = "Start";
            }
            else if (netItem.Player == 0)
            {
                sender = "Archipelago";
            }
            else
            {
                sender = session.Players.GetPlayerName(netItem.Player);
            }
            InteropTag recentItemsTag = item.AddTag<InteropTag>();
            recentItemsTag.Message = "RecentItems";
            recentItemsTag.Properties["DisplaySource"] = sender;

            RemotePlacement pmt = RemotePlacement.GetOrAddSingleton();
            item.Load();
            pmt.Add(item);

            UIDef def = item.GetResolvedUIDef();
            item.Give(pmt, SilentGiveInfo.Clone());

            if (netItem.Location != -2) // Don't message startinventory.
            {
                MessageController.Enqueue(def.GetSprite(), $"{def.GetPostviewName()} from {sender}");
            }
        }

        /// <summary>
        /// With DeathLink (and possibly with future trap implementations), dying in certain locations can produce inaccessible shades.  Fix that.
        /// </summary>
        private void ModHooks_AfterPlayerDeadHook()
        {
            // Fixes up some bad shade placements by vanilla HK.
            PlayerData pd = PlayerData.instance;
            if (ShadeSpawnPositionFixes.TryGetValue(pd.shadeScene, out (float x, float y) position))
            {
                pd.shadePositionX = position.x;
                pd.shadePositionY = position.y;
            }
        }

        public void DisconnectArchipelago()
        {
            if (session?.Socket != null)
            {
                session.Socket.SocketClosed -= Socket_SocketClosed;
            }

            DeathLinkSupport.Instance.Disable();
            Slot = 0;
            AllSlots = null;

            if (session?.Socket != null && session.Socket.Connected)
            {
                session.Socket.DisconnectAsync();
            }

            session = null;
        }

        /// <summary>
        /// Begin deferring location checks.
        /// </summary>
        /// <remarks>
        /// During initial synchronization and other cases, we want to collect individual locations and send them as one batch.  This begins that process.
        /// 
        /// Locations marked as obtained during deferred location checks will also have their messaging suppressed unless they are for our own world.
        /// </remarks>
        public void DeferLocationChecks()
        {
            DeferringLocationChecks = true;
            LogDebug("Deferring location checks");
        }

        /// <summary>
        /// Stop deferring location checks.
        /// </summary>
        public void StopDeferringLocationChecks()
        {
            LogDebug("No longer deferring location checks");
            DeferringLocationChecks = false;
            if (deferredLocationChecks.Any())
            {
                LogDebug($"Sending {deferredLocationChecks.Count} deferred location check(s).");
                session.Locations.CompleteLocationChecks(deferredLocationChecks.ToArray());
                deferredLocationChecks.Clear();
            }
        }

        public void ReportDisconnect()
        {
            ItemChanger.Internal.MessageController.Enqueue(
                null,
                "Error: Lost connection to Archipelago server"
            );
            ItemChanger.Internal.MessageController.Enqueue(
                null,
                "Reload your save to attempt to reconnect."
            );
        }


        /// <summary>
        /// Checks a single location or adds it to the deferred list.
        /// </summary>
        public void CheckLocation(long locationID)
        {
            if (locationID == 0)
            {
                throw new Exception("CheckLocation called with unspecified locationID.  This should never happen.");
            }

            if (DeferringLocationChecks)
            {
                deferredLocationChecks.Add(locationID);
            }
            else
            {
                try
                {
                    session.Locations.CompleteLocationChecks(locationID);
                }
                catch (ArchipelagoSocketClosedException)
                {
                    ReportDisconnect();
                }
            }
        }

        public void SendPlacementHints()
        {
            if (!PendingPlacementHints.Any())
            {
                return;
            }

            HashSet<ArchipelagoItemTag> hintedTags = new();
            HashSet<long> hintedLocationIDs = new();
            ArchipelagoItemTag tag;

            foreach (AbstractPlacement pmt in PendingPlacementHints)
            {
                foreach (AbstractItem item in pmt.Items)
                {
                    if (item.GetTag<ArchipelagoItemTag>(out tag) && !tag.Hinted)
                    {
                        if ((tag.Flags.HasFlag(ItemFlags.Advancement) || tag.Flags.HasFlag(ItemFlags.NeverExclude))
                            && !item.WasEverObtained()
                            && !item.HasTag<DisableItemPreviewTag>())
                        {
                            hintedTags.Add(tag);
                            hintedLocationIDs.Add(tag.Location);
                        }
                        else
                        {
                            tag.Hinted = true;
                        }
                    }
                }
            }

            PendingPlacementHints.Clear();
            if (!hintedLocationIDs.Any())
            {
                return;
            }

            LogDebug($"Hinting {hintedLocationIDs.Count()} locations.");
            try
            {
                session.Socket.SendPacketAsync(new LocationScoutsPacket()
                {
                    CreateAsHint = true,
                    Locations = hintedLocationIDs.ToArray(),
                }).ContinueWith(x =>
                {
                    bool result = !x.IsFaulted;
                    foreach (ArchipelagoItemTag tag in hintedTags)
                    {
                        tag.Hinted = result;
                    }
                }).Wait();
            }
            catch (ArchipelagoSocketClosedException)
            {
                ReportDisconnect();
            }
        }

        /// <summary>
        /// Called when loading local (game-specific save data)
        /// </summary>
        /// <remarks>
        /// This is also called on the main menu screen with empty (defaulted) ConnectionDetails.  This will have an empty SlotName, so we treat this as a noop.
        /// </remarks>
        /// <param name="details"></param>
        public void OnLoadLocal(ConnectionDetails details)
        {
            if (details.SlotName == null ||
                details.SlotName == "") // Apparently, this is called even before a save is loaded.  Catch this.
            {
                return;
            }

            ApSettings = details;
        }

        /// <summary>
        /// Called when saving local (game-specific) save data.
        /// </summary>
        /// <returns></returns>
        public ConnectionDetails OnSaveLocal()
        {
            if (!ArchipelagoEnabled)
            {
                return default;
            }

            return ApSettings;
        }

        /// <summary>
        /// Called when loading global save data.
        /// </summary>
        /// <remarks>
        /// For simplicity's sake, we use the same data structure for both global and local save data, though not all fields are relevant in the global context.
        /// </remarks>
        /// <param name="details"></param>
        public void OnLoadGlobal(ConnectionDetails details)
        {
            MenuSettings = details;
            MenuSettings.ItemIndex = 0;
        }

        /// <summary>
        /// Called when saving global save data.
        /// </summary>
        /// <returns></returns>
        public ConnectionDetails OnSaveGlobal()
        {
            return new ConnectionDetails()
            {
                ServerUrl = MenuSettings.ServerUrl,
                ServerPort = MenuSettings.ServerPort,
                SlotName = MenuSettings.SlotName
            };
        }
    }
}