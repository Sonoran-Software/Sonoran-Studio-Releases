using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Rage;
using Rage.Native;

[assembly: Rage.Attributes.Plugin(
    "Sonoran Studio LSPDFR Integration",
    Description = "Synchronizes LSPDFR lighting, unit, and callout data with Sonoran Studio.",
    Author = "Sonoran Software",
    EntryPoint = "SonoranStudio.LSPDFR.EntryPoint.Main",
    ExitPoint = "SonoranStudio.LSPDFR.EntryPoint.OnUnload",
    PrefersSingleInstance = true,
    SupportUrl = "https://docs.sonoransoftware.com/studio/sonoran-studio/smart-lighting#lspdfr")]

namespace SonoranStudio.LSPDFR
{
    public static class EntryPoint
    {
        private const string Endpoint = "http://127.0.0.1:9990/lspdfr";
        private const float OnSceneDistance = 75f;
        private static readonly BlockingCollection<PluginMessage> Outbox = new BlockingCollection<PluginMessage>(new ConcurrentQueue<PluginMessage>(), 100);
        private static readonly HttpClient Client = CreateClient();
        private static readonly List<KeyValuePair<EventInfo, Delegate>> EventHandlers = new List<KeyValuePair<EventInfo, Delegate>>();
        private static readonly Regex FormattingCode = new Regex("~[^~]*~", RegexOptions.Compiled);
        private static Thread? sender;
        private static volatile bool stopping;
        private static bool studioUnavailable;
        private static string? senderNotice;
        private static int resyncRequested;
        private static Type? functionsType;
        private static Type? eventsType;
        private static bool lspdfrReady;
        private static bool playerOnDuty;
        private static bool dutyKnown;
        private static CalloutSnapshot? currentCallout;
        private static string? previousLightingState;
        private static PlayerSnapshot? previousPlayerState;
        private static string? previousUnitIdentity;
        private static string? previousUnitLocation;
        private static DateTime nextUnitUpdate = DateTime.MinValue;
        private static DateTime nextPlayerUpdate = DateTime.MinValue;
        private static DateTime nextHealthSample = DateTime.MinValue;
        private static DateTime nextHeartbeat = DateTime.MinValue;

        public static void Main()
        {
            sender = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "Sonoran Studio LSPDFR sender"
            };
            sender.Start();

            Game.LogTrivial("[Sonoran Studio] LSPDFR integration loaded.");
            while (!stopping)
            {
                try
                {
                    if (!lspdfrReady)
                    {
                        TryInitializeLspdfr();
                    }

                    string lightingState = ReadVehicleState();
                    if (!String.Equals(previousLightingState, lightingState, StringComparison.Ordinal))
                    {
                        previousLightingState = lightingState;
                        QueueMessage(new PluginMessage { State = lightingState });
                        nextHeartbeat = DateTime.UtcNow.AddSeconds(15);
                    }
                    else if (DateTime.UtcNow >= nextHeartbeat)
                    {
                        QueueMessage(new PluginMessage { State = lightingState });
                        nextHeartbeat = DateTime.UtcNow.AddSeconds(15);
                    }

                    if (DateTime.UtcNow >= nextPlayerUpdate)
                    {
                        nextPlayerUpdate = DateTime.UtcNow.AddMilliseconds(250);
                        TrackPlayerMoments();
                    }

                    if (lspdfrReady && DateTime.UtcNow >= nextUnitUpdate)
                    {
                        nextUnitUpdate = DateTime.UtcNow.AddSeconds(1);
                        RecoverCurrentCallout();
                        QueueUnitUpdate(false);
                    }
                    if (Interlocked.Exchange(ref resyncRequested, 0) == 1)
                    {
                        QueueUnitUpdate(true);
                        if (currentCallout?.Accepted == true)
                        {
                            QueueOverlayEvent("call.attached", currentCallout.ToAttachedData());
                        }
                    }
                }
                catch (Exception error)
                {
                    Game.LogTrivial("[Sonoran Studio] Could not read LSPDFR state: " + error.Message);
                }

                string? notice = Interlocked.Exchange(ref senderNotice, null);
                if (notice != null)
                {
                    Game.LogTrivial(notice);
                }
                GameFiber.Sleep(100);
            }
        }

        public static void OnUnload(bool isTerminating)
        {
            if (stopping)
            {
                return;
            }

            UnsubscribeLspdfrEvents();
            QueueMessage(new PluginMessage { State = "restore" });
            stopping = true;
            Outbox.CompleteAdding();
            if (!isTerminating && sender != null)
            {
                sender.Join(1500);
            }
        }

        private static void TryInitializeLspdfr()
        {
            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => String.Equals(candidate.GetName().Name, "LSPD First Response", StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                return;
            }

            functionsType = assembly.GetType("LSPD_First_Response.Mod.API.Functions", false);
            eventsType = assembly.GetType("LSPD_First_Response.Mod.API.Events", false);
            if (functionsType == null || eventsType == null)
            {
                return;
            }

            BindEvent(functionsType, "OnOnDutyStateChanged", nameof(HandleDutyChanged));
            BindEvent(eventsType, "OnCalloutDisplayed", nameof(HandleCalloutDisplayed));
            BindEvent(eventsType, "OnCalloutAccepted", nameof(HandleCalloutAccepted));
            BindEvent(eventsType, "OnCalloutFinished", nameof(HandleCalloutFinished));
            BindEvent(eventsType, "OnCalloutNotAccepted", nameof(HandleCalloutNotAccepted));
            lspdfrReady = true;
            previousUnitIdentity = null;
            previousUnitLocation = null;
            Game.LogTrivial("[Sonoran Studio] LSPDFR unit and callout events connected.");
        }

        private static void BindEvent(Type owner, string eventName, string handlerName)
        {
            EventInfo? eventInfo = owner.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
            MethodInfo? handler = typeof(EntryPoint).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? invoke = eventInfo?.EventHandlerType?.GetMethod("Invoke");
            ParameterInfo[]? parameters = invoke?.GetParameters();
            if (eventInfo?.EventHandlerType == null || handler == null || parameters == null || parameters.Length != 1)
            {
                return;
            }
            ParameterExpression handle = Expression.Parameter(parameters[0].ParameterType, "handle");
            MethodCallExpression call = Expression.Call(handler, Expression.Convert(handle, typeof(object)));
            Delegate callback = Expression.Lambda(eventInfo.EventHandlerType, call, handle).Compile();
            eventInfo.AddEventHandler(null, callback);
            EventHandlers.Add(new KeyValuePair<EventInfo, Delegate>(eventInfo, callback));
        }

        private static void UnsubscribeLspdfrEvents()
        {
            foreach (KeyValuePair<EventInfo, Delegate> item in EventHandlers)
            {
                try { item.Key.RemoveEventHandler(null, item.Value); }
                catch { /* LSPDFR may already be unloading. */ }
            }
            EventHandlers.Clear();
        }

        private static void HandleCalloutDisplayed(object handle)
        {
            CalloutSnapshot? snapshot = ReadCallout(handle, false);
            if (snapshot == null)
            {
                return;
            }
            currentCallout = snapshot;
            QueueOverlayEvent("call.displayed", snapshot.ToDisplayedData());
        }

        private static void HandleDutyChanged(object value)
        {
            if (value is not bool onDuty)
            {
                return;
            }
            playerOnDuty = onDuty;
            dutyKnown = true;
            if (!onDuty)
            {
                if (currentCallout?.Accepted == true)
                {
                    QueueOverlayEvent("call.detached", new EventData { CallId = currentCallout.Id });
                }
                currentCallout = null;
            }
            QueueUnitUpdate(true);
        }

        private static void HandleCalloutAccepted(object handle)
        {
            playerOnDuty = true;
            dutyKnown = true;
            CalloutSnapshot? snapshot = ReadCallout(handle, true) ?? currentCallout;
            if (snapshot == null)
            {
                return;
            }
            snapshot.Accepted = true;
            currentCallout = snapshot;
            QueueOverlayEvent("call.attached", snapshot.ToAttachedData());
            QueueUnitUpdate(true);
        }

        private static void HandleCalloutFinished(object handle)
        {
            CalloutSnapshot? snapshot = currentCallout ?? ReadCallout(handle, true);
            if (snapshot?.Accepted == true)
            {
                QueueOverlayEvent("call.detached", new EventData { CallId = snapshot.Id });
            }
            currentCallout = null;
            QueueUnitUpdate(true);
        }

        private static void HandleCalloutNotAccepted(object handle)
        {
            currentCallout = null;
            QueueUnitUpdate(true);
        }

        private static void RecoverCurrentCallout()
        {
            if (currentCallout != null || InvokeFunction("IsCalloutRunning") is not bool running || !running)
            {
                return;
            }
            object? handle = InvokeFunction("GetCurrentCallout");
            if (handle == null)
            {
                return;
            }
            string acceptance = Convert.ToString(ReadProperty(GetHandleObject(handle), "AcceptanceState")) ?? "";
            bool accepted = acceptance.IndexOf("Accepted", StringComparison.OrdinalIgnoreCase) >= 0
                && acceptance.IndexOf("Not", StringComparison.OrdinalIgnoreCase) < 0;
            CalloutSnapshot? snapshot = ReadCallout(handle, accepted);
            if (snapshot == null)
            {
                return;
            }
            currentCallout = snapshot;
            if (accepted)
            {
                QueueOverlayEvent("call.attached", snapshot.ToAttachedData());
            }
        }

        private static CalloutSnapshot? ReadCallout(object handle, bool accepted)
        {
            object? callout = GetHandleObject(handle);
            string title = CleanText(Convert.ToString(ReadProperty(callout, "FriendlyName")));
            if (String.IsNullOrWhiteSpace(title))
            {
                title = CleanText(Convert.ToString(InvokeFunction("GetCalloutName", handle)));
            }
            string message = CleanText(Convert.ToString(ReadProperty(callout, "CalloutMessage")));
            string advisory = CleanText(Convert.ToString(ReadProperty(callout, "CalloutAdvisory")));
            if (String.IsNullOrWhiteSpace(title))
            {
                title = !String.IsNullOrWhiteSpace(message) ? message : "LSPDFR Callout";
            }

            Vector3? position = ReadProperty(callout, "CalloutPosition") is Vector3 vector ? vector : (Vector3?)null;
            string id = currentCallout != null && Equals(currentCallout.Handle, handle)
                ? currentCallout.Id
                : "LSPDFR-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            return new CalloutSnapshot
            {
                Handle = handle,
                Id = id,
                Title = title,
                Message = String.Equals(message, title, StringComparison.OrdinalIgnoreCase) ? "" : message,
                Advisory = advisory,
                Location = position.HasValue ? ReadLocation(position.Value) : "",
                Position = position,
                Accepted = accepted
            };
        }

        private static object? GetHandleObject(object handle)
        {
            try
            {
                PropertyInfo? property = handle.GetType().GetProperty("Object", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.GetGetMethod(true)?.Invoke(handle, null);
            }
            catch { return null; }
        }

        private static object? ReadProperty(object? target, string name)
        {
            if (target == null)
            {
                return null;
            }
            try
            {
                return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target, null);
            }
            catch { return null; }
        }

        private static object? InvokeFunction(string name, params object[] args)
        {
            if (functionsType == null)
            {
                return null;
            }
            try
            {
                MethodInfo? method = functionsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(candidate => candidate.Name == name)
                    .FirstOrDefault(candidate =>
                    {
                        ParameterInfo[] parameters = candidate.GetParameters();
                        if (parameters.Length != args.Length)
                        {
                            return false;
                        }
                        for (int index = 0; index < parameters.Length; index++)
                        {
                            if (args[index] != null && !parameters[index].ParameterType.IsInstanceOfType(args[index]))
                            {
                                return false;
                            }
                        }
                        return true;
                    });
                return method?.Invoke(null, args);
            }
            catch { return null; }
        }

        private static void QueueUnitUpdate(bool force)
        {
            string displayName = "";
            string department = CleanText(Convert.ToString(InvokeFunction("GetCurrentAgencyScriptName")));
            bool onDuty = playerOnDuty || (!dutyKnown && !String.IsNullOrWhiteSpace(department));
            string status = onDuty ? CurrentUnitStatus() : "UNAVAILABLE";
            string location = "";
            Ped player = Game.LocalPlayer.Character;
            if (player != null && player.Exists())
            {
                object? persona = InvokeFunction("GetPersonaForPed", player);
                displayName = CleanText(Convert.ToString(ReadProperty(persona, "FullName")));
                location = ReadLocation(player.Position);
            }
            if (!onDuty) department = "";

            string identity = displayName + "|" + department + "|" + status;
            if (force || !String.Equals(previousUnitIdentity, identity, StringComparison.Ordinal))
            {
                previousUnitIdentity = identity;
                previousUnitLocation = location;
                QueueOverlayEvent("unit.updated", new EventData
                {
                    DisplayName = displayName,
                    Department = department,
                    Status = status,
                    Location = location
                });
            }
            else if (!String.IsNullOrWhiteSpace(location) && !String.Equals(previousUnitLocation, location, StringComparison.Ordinal))
            {
                previousUnitLocation = location;
                QueueOverlayEvent("unit.updated", new EventData { Location = location });
            }
        }

        private static string CurrentUnitStatus()
        {
            if (currentCallout?.Accepted != true)
            {
                return "AVAILABLE";
            }
            if (currentCallout.Position.HasValue)
            {
                Vector3 delta = Game.LocalPlayer.Character.Position - currentCallout.Position.Value;
                if (delta.Length() <= OnSceneDistance)
                {
                    return "ON SCENE";
                }
            }
            return "EN ROUTE";
        }

        private static string ReadLocation(Vector3 position)
        {
            string street = "";
            string area = "";
            try { street = CleanText(World.GetStreetName(World.GetStreetHash(position))); }
            catch { /* Street names are best-effort. */ }
            object? zone = InvokeFunction("GetZoneAtPosition", position);
            area = CleanText(Convert.ToString(ReadProperty(zone, "RealAreaName")));
            if (String.Equals(street, area, StringComparison.OrdinalIgnoreCase))
            {
                area = "";
            }
            return String.Join(" · ", new[] { street, area }.Where(value => !String.IsNullOrWhiteSpace(value)));
        }

        private static string CleanText(string? value)
        {
            return FormattingCode.Replace(value ?? "", "").Trim();
        }

        private static string ReadVehicleState()
        {
            Ped player = Game.LocalPlayer.Character;
            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists() || !vehicle.IsPoliceVehicle)
            {
                return "restore";
            }
            if (vehicle.IsSirenOn)
            {
                return "lights";
            }
            int indicators = NativeFunction.Natives.GET_VEHICLE_INDICATOR_LIGHTS<int>(vehicle);
            switch (indicators)
            {
                case 1: return "left";
                case 2: return "right";
                case 3: return "hazard";
                default: return "restore";
            }
        }

        private static void TrackPlayerMoments()
        {
            PlayerSnapshot? current = ReadPlayerSnapshot();
            PlayerSnapshot? previous = previousPlayerState;
            if (current == null)
            {
                return;
            }
            previousPlayerState = current;
            if (previous == null)
            {
                if (!current.Dead)
                {
                    QueueGameMoment("game.health.sample", current.HealthData());
                    nextHealthSample = DateTime.UtcNow.AddSeconds(5);
                }
                return;
            }

            if (current.Dead != previous.Dead)
            {
                QueueGameMoment(current.Dead ? "game.player.died" : "game.player.returned", current.HealthData());
                nextHealthSample = DateTime.MinValue;
                return;
            }
            if (current.Dead)
            {
                return;
            }

            if (current.Armed != previous.Armed)
            {
                QueueGameMoment(current.Armed ? "game.weapon.drawn" : "game.weapon.holstered", new GameMomentData
                {
                    WeaponHash = current.Armed ? current.WeaponHash : previous.WeaponHash
                });
            }
            else if (current.Armed && current.WeaponHash != previous.WeaponHash)
            {
                QueueGameMoment("game.weapon.holstered", new GameMomentData { WeaponHash = previous.WeaponHash });
                QueueGameMoment("game.weapon.drawn", new GameMomentData { WeaponHash = current.WeaponHash });
            }

            if (!String.Equals(current.TravelMode, previous.TravelMode, StringComparison.Ordinal))
            {
                QueueGameMoment("game.travel." + current.TravelMode, current.TravelData());
            }
            if (current.HealthPercent != previous.HealthPercent || DateTime.UtcNow >= nextHealthSample)
            {
                QueueGameMoment("game.health.sample", current.HealthData());
                nextHealthSample = DateTime.UtcNow.AddSeconds(5);
            }
        }

        private static PlayerSnapshot? ReadPlayerSnapshot()
        {
            Ped player = Game.LocalPlayer.Character;
            if (player == null || !player.Exists())
            {
                return null;
            }

            bool dead = NativeFunction.Natives.IS_ENTITY_DEAD<bool>(player);
            int health = NativeFunction.Natives.GET_ENTITY_HEALTH<int>(player);
            int maximumHealth = Math.Max(NativeFunction.Natives.GET_ENTITY_MAX_HEALTH<int>(player), 1);
            int healthFloor = maximumHealth > 100 ? 100 : 0;
            int usableMaximum = Math.Max(maximumHealth - healthFloor, 1);
            int healthPercent = Math.Max(0, Math.Min(100, (int)Math.Round(((health - healthFloor) / (double)usableMaximum) * 100)));
            bool armed = !dead && NativeFunction.Natives.IS_PED_ARMED<bool>(player, 7);

            string travelMode = "on_foot";
            int? vehicleClass = null;
            uint? vehicleModel = null;
            if (NativeFunction.Natives.IS_PED_IN_ANY_VEHICLE<bool>(player, false))
            {
                Vehicle vehicle = player.CurrentVehicle;
                if (vehicle != null && vehicle.Exists())
                {
                    vehicleClass = NativeFunction.Natives.GET_VEHICLE_CLASS<int>(vehicle);
                    vehicleModel = NativeFunction.Natives.GET_ENTITY_MODEL<uint>(vehicle);
                    travelMode = vehicleClass == 14 ? "watercraft" : vehicleClass == 15 || vehicleClass == 16 ? "aircraft" : "vehicle";
                }
            }

            return new PlayerSnapshot
            {
                Dead = dead,
                Armed = armed,
                WeaponHash = armed ? NativeFunction.Natives.GET_SELECTED_PED_WEAPON<uint>(player) : (uint?)null,
                Health = health,
                MaximumHealth = maximumHealth,
                HealthPercent = healthPercent,
                TravelMode = travelMode,
                VehicleClass = vehicleClass,
                VehicleModel = vehicleModel
            };
        }

        private static void QueueGameMoment(string eventName, GameMomentData data)
        {
            QueueMessage(new PluginMessage
            {
                State = previousLightingState ?? ReadVehicleState(),
                GameEvent = new GameMoment { EventName = eventName, Args = data }
            });
        }

        private static void QueueOverlayEvent(string type, EventData data)
        {
            QueueMessage(new PluginMessage
            {
                State = previousLightingState ?? ReadVehicleState(),
                Event = new OverlayEvent
                {
                    SchemaVersion = 1,
                    EventId = Guid.NewGuid().ToString("N"),
                    Type = type,
                    OccurredAt = DateTime.UtcNow.ToString("o"),
                    Data = data
                }
            });
        }

        private static void QueueMessage(PluginMessage message)
        {
            if (stopping || Outbox.IsAddingCompleted)
            {
                return;
            }
            try { Outbox.TryAdd(message); }
            catch (InvalidOperationException) { /* The plugin is already unloading. */ }
        }

        private static void SenderLoop()
        {
            foreach (PluginMessage message in Outbox.GetConsumingEnumerable())
            {
                while (!SendMessage(message) && !stopping)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        private static bool SendMessage(PluginMessage message)
        {
            try
            {
                using (StringContent body = new StringContent(Serialize(message), Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = Client.PostAsync(Endpoint, body).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                }
                if (studioUnavailable)
                {
                    studioUnavailable = false;
                    senderNotice = "[Sonoran Studio] Desktop connection restored.";
                    Interlocked.Exchange(ref resyncRequested, 1);
                }
                return true;
            }
            catch (Exception error)
            {
                if (!studioUnavailable)
                {
                    studioUnavailable = true;
                    senderNotice = "[Sonoran Studio] Desktop app is unavailable: " + error.Message;
                }
                return false;
            }
        }

        private static string Serialize(PluginMessage message)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(PluginMessage));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, message);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(750) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SonoranStudio-LSPDFR/1.1");
            return client;
        }

        [DataContract]
        private sealed class PluginMessage
        {
            [DataMember(Name = "state")]
            public string State { get; set; } = "restore";

            [DataMember(Name = "event", EmitDefaultValue = false)]
            public OverlayEvent? Event { get; set; }

            [DataMember(Name = "gameEvent", EmitDefaultValue = false)]
            public GameMoment? GameEvent { get; set; }
        }

        [DataContract]
        private sealed class GameMoment
        {
            [DataMember(Name = "event")]
            public string EventName { get; set; } = "";

            [DataMember(Name = "args")]
            public GameMomentData Args { get; set; } = new GameMomentData();
        }

        [DataContract]
        private sealed class GameMomentData
        {
            [DataMember(Name = "weaponHash", EmitDefaultValue = false)] public uint? WeaponHash { get; set; }
            [DataMember(Name = "health", EmitDefaultValue = false)] public int? Health { get; set; }
            [DataMember(Name = "maximumHealth", EmitDefaultValue = false)] public int? MaximumHealth { get; set; }
            [DataMember(Name = "healthPercent", EmitDefaultValue = false)] public int? HealthPercent { get; set; }
            [DataMember(Name = "travelMode", EmitDefaultValue = false)] public string? TravelMode { get; set; }
            [DataMember(Name = "vehicleClass", EmitDefaultValue = false)] public int? VehicleClass { get; set; }
            [DataMember(Name = "vehicleModel", EmitDefaultValue = false)] public uint? VehicleModel { get; set; }
        }

        private sealed class PlayerSnapshot
        {
            public bool Dead { get; set; }
            public bool Armed { get; set; }
            public uint? WeaponHash { get; set; }
            public int Health { get; set; }
            public int MaximumHealth { get; set; }
            public int HealthPercent { get; set; }
            public string TravelMode { get; set; } = "on_foot";
            public int? VehicleClass { get; set; }
            public uint? VehicleModel { get; set; }

            public GameMomentData HealthData()
            {
                return new GameMomentData
                {
                    Health = Health,
                    MaximumHealth = MaximumHealth,
                    HealthPercent = HealthPercent
                };
            }

            public GameMomentData TravelData()
            {
                return new GameMomentData
                {
                    TravelMode = TravelMode,
                    VehicleClass = VehicleClass,
                    VehicleModel = VehicleModel
                };
            }
        }

        [DataContract]
        private sealed class OverlayEvent
        {
            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "eventId")]
            public string EventId { get; set; } = "";

            [DataMember(Name = "type")]
            public string Type { get; set; } = "";

            [DataMember(Name = "occurredAt")]
            public string OccurredAt { get; set; } = "";

            [DataMember(Name = "data")]
            public EventData Data { get; set; } = new EventData();
        }

        [DataContract]
        private sealed class EventData
        {
            [DataMember(Name = "displayName", EmitDefaultValue = false)] public string? DisplayName { get; set; }
            [DataMember(Name = "department", EmitDefaultValue = false)] public string? Department { get; set; }
            [DataMember(Name = "status", EmitDefaultValue = false)] public string? Status { get; set; }
            [DataMember(Name = "location", EmitDefaultValue = false)] public string? Location { get; set; }
            [DataMember(Name = "callId", EmitDefaultValue = false)] public string? CallId { get; set; }
            [DataMember(Name = "title", EmitDefaultValue = false)] public string? Title { get; set; }
            [DataMember(Name = "message", EmitDefaultValue = false)] public string? Message { get; set; }
            [DataMember(Name = "advisory", EmitDefaultValue = false)] public string? Advisory { get; set; }
        }

        private sealed class CalloutSnapshot
        {
            public object? Handle { get; set; }
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string Message { get; set; } = "";
            public string Advisory { get; set; } = "";
            public string Location { get; set; } = "";
            public Vector3? Position { get; set; }
            public bool Accepted { get; set; }

            public EventData ToDisplayedData()
            {
                return new EventData { CallId = Id, Title = Title, Message = Message, Advisory = Advisory, Location = Location };
            }

            public EventData ToAttachedData()
            {
                return new EventData { CallId = Id, Title = Title, Location = Location };
            }
        }
    }
}
