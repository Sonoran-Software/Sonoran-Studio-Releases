using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using Rage;
using Rage.Native;

[assembly: Rage.Attributes.Plugin(
    "Sonoran Studio LSPDFR Lighting",
    Description = "Synchronizes LSPDFR vehicle lights with Sonoran Studio smart lighting.",
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
        private static readonly BlockingCollection<string> Outbox = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private static readonly HttpClient Client = CreateClient();
        private static Thread? sender;
        private static volatile bool stopping;
        private static bool studioUnavailable;
        private static string? senderNotice;

        public static void Main()
        {
            sender = new Thread(SenderLoop)
            {
                IsBackground = true,
                Name = "Sonoran Studio lighting sender"
            };
            sender.Start();

            Game.LogTrivial("[Sonoran Studio] LSPDFR lighting integration loaded.");
            string? previous = null;

            while (true)
            {
                try
                {
                    string state = ReadVehicleState();
                    if (!String.Equals(previous, state, StringComparison.Ordinal))
                    {
                        previous = state;
                        QueueState(state);
                    }
                }
                catch (Exception error)
                {
                    Game.LogTrivial("[Sonoran Studio] Could not read vehicle lighting: " + error.Message);
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

            QueueState("restore");
            stopping = true;
            Outbox.CompleteAdding();
            if (!isTerminating && sender != null)
            {
                sender.Join(1000);
            }
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

        private static void QueueState(string state)
        {
            if (stopping || Outbox.IsAddingCompleted)
            {
                return;
            }

            try
            {
                Outbox.Add(state);
            }
            catch (InvalidOperationException)
            {
                // The plugin is already unloading.
            }
        }

        private static void SenderLoop()
        {
            while (!stopping || Outbox.Count > 0)
            {
                string state;
                if (!Outbox.TryTake(out state, 250))
                {
                    continue;
                }

                string newer;
                while (Outbox.TryTake(out newer))
                {
                    state = newer;
                }

                SendState(state);
            }
        }

        private static void SendState(string state)
        {
            try
            {
                using (StringContent body = new StringContent("{\"state\":\"" + state + "\"}", Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = Client.PostAsync(Endpoint, body).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                }

                if (studioUnavailable)
                {
                    studioUnavailable = false;
                    senderNotice = "[Sonoran Studio] Desktop lighting connection restored.";
                }
            }
            catch (Exception error)
            {
                if (!studioUnavailable)
                {
                    studioUnavailable = true;
                    senderNotice = "[Sonoran Studio] Desktop app is unavailable: " + error.Message;
                }
            }
        }

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(750)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SonoranStudio-LSPDFR/1.0");
            return client;
        }
    }
}
