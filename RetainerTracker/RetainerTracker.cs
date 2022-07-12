using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.RetainerList;

namespace RetainerTracker
{
    public sealed unsafe class RetainerTracker : IDalamudPlugin
    {
        public string Name => "RetainerTracker";

        private const string commandName = "/rstatus";

        [PluginService] private static ChatGui ChatGui { get; set; } = null!;
        [PluginService] private static CommandManager CommandManager { get; set; } = null!;
        [PluginService] public static ClientState ClientState { get; private set; } = null!;
        [PluginService] public static Framework Framework { get; private set; } = null!;

        private RetainerManager* rm;

        private delegate IntPtr RetainerVentureCompleteChangeDelegate(IntPtr data);
        private Hook<RetainerVentureCompleteChangeDelegate>[] RetainerHooks = new Hook<RetainerVentureCompleteChangeDelegate>[10];

        private bool[] VentureWasCompleted = new bool[10];
        private Task[] notifyTaskPerRetainer = new Task[10];
        private CancellationTokenSource[] cancelTokenPerRetainer = new CancellationTokenSource[10];
        private string[] retainerNames = new string[10];
        private ulong lastChar = 0;
        private int currentRetainerCount = 0;

        public RetainerTracker()
        {
            Resolver.Initialize();
            rm = RetainerManager.Instance();

            string helpMsg = "You need to open up the Timers window or interact with a summoning bell to populate retainer data everytime you login. " +
                "Text is shown in the Retainer Sales text channel (Character Configuration -> Log Window Settings -> Log Filters)";

            CommandManager.AddHandler(commandName, new CommandInfo((cmd, args) =>
            {
                PluginLog.LogInformation($"Test: Retainers Available = {currentRetainerCount}");
                if (currentRetainerCount > 0)
                {
                    for (int i = 0; i < currentRetainerCount; i++)
                    {
                        SendChatEntry(rm->Retainer[i], i, showTime: true);
                    }
                }
                else
                {
                    PrintChatMessage(helpMsg);
                }
            })
            {
                HelpMessage = helpMsg
            });

            
            Framework.Update += OnFrameworkUpdate;
        }

        private void OnFrameworkUpdate(Framework framework)
        {
            if (ClientState.LocalContentId == lastChar)
            {
                return;
            }    

            if (!ClientState.IsLoggedIn || ClientState.LocalPlayer == null)
            {
                //canPingDiscord = false;
                return;
            }

            CleanupOldTasks();

            int i;
            Retainer* retainerPtr;
            Retainer retainer;

            for (i = 0; i < 10; i++)
            {
                retainerPtr = rm->Retainer[i];
                retainer = *retainerPtr;
                PluginLog.Information($"retainer {i + 1} available?: {retainer.Available}");
                if (retainer.Available != 1)
                {
                    break;
                }

                retainerNames[i] = GetRetainerName(retainerPtr->Name);

                // Send Chat when venture should complete
                var ventureCompleteOn = ConvertFromUnixTimestamp(retainer.VentureComplete);
                if (ventureCompleteOn > DateTime.UtcNow)
                {
                    // canPingDisc = true;

                    int delay = (int)(ventureCompleteOn - DateTime.UtcNow).TotalMilliseconds + 1000;
                    CreateNotifyTask(retainerPtr, i, delay, showTime: true);
                }

                SendChatEntry(retainerPtr, i, showTime: true);

                // https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Game/RetainerManager.cs
                /*
                 Error while loading Retainer Tracker
                    System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.
                    ---> System.Exception: Reloaded Hooks: Internal Error in Internal/IcedPatcher. Failed to re-encode code for new address.
                            Process will probably die.Error: Can't encode an invalid instruction : 0x7FF6B795F2EC (bad)
                       at Reloaded.Hooks.Internal.IcedPatcher.EncodeForNewAddress(IntPtr newAddress)
                 */

                // Is this cuz im trying to hook a var instead of a method?

                //var retainersVentureCompleteOnPtr = IntPtr.Add((IntPtr)rm, 0x48 * i + 0x3C);
                //PluginLog.Information($"RM: {string.Format("0x{0:X}", (IntPtr)rm)}; RVC: {string.Format("0x{0:X}", retainersVentureCompleteOnPtr)}");
                //RetainerHooks[i] = new Hook<RetainerVentureCompleteChangeDelegate>(retainersVentureCompleteOnPtr,
                //    new RetainerVentureCompleteChangeDelegate((data) => RetainerVentureCompleteChangeDetour(data, retainerPtr, i)));
                //RetainerHooks[i].Enable();
            }

            // We found retainers initialized (user needs to either open timers window [default: ctrl+u] or visit a summoning bell) so no need to update on framework anymore
            if (i > 0)
            {
                currentRetainerCount = i;
                lastChar = ClientState.LocalContentId;
            }
        }

        private IntPtr RetainerVentureCompleteChangeDetour(IntPtr data, Retainer* retainerPtr, int i)
        {
            SendChatEntry(retainerPtr, i);
            return RetainerHooks[0].Original(data);
        }

        private void SendChatEntry(Retainer* retainerPtr, int retainerNumber, bool showTime = false)
        {
            var retainer = *retainerPtr;
            var ventureCompleteOn = ConvertFromUnixTimestamp(retainer.VentureComplete);
            
            ///PluginLog.Information($"Retainer {retainerNumber}: VentureComplete={ventureCompleteOn}; CompleteIn: {ventureCompleteInStr}; Now: {DateTime.UtcNow}; VentureWasCompleted={VentureWasCompleted[retainerNumber]}; Gil:  {retainer.Gil}");
            PluginLog.Information($"{retainerNames[retainerNumber]} ({retainerNumber}): avail={retainer.Available}; level=; ventureId={retainer.VentureID}; showTime={showTime}");

            if (ventureCompleteOn <= DateTime.UtcNow)
            {
                if (!VentureWasCompleted[retainerNumber] || showTime)
                {
                    PrintChatMessage($"{retainerNames[retainerNumber]} has completed their Venture.");
                }
                VentureWasCompleted[retainerNumber] = true;

                // Todo: remove when can figure out hooks
                // Check again an hour from now
                int delay = (int)(TimeSpan.FromHours(1).TotalMilliseconds);
                CreateNotifyTask(retainerPtr, retainerNumber, delay);

                return;
            }
            else if (ventureCompleteOn > DateTime.UtcNow && VentureWasCompleted[retainerNumber])
            {
                // Todo: remove when can figure out hooks
                int delay = (int)(ventureCompleteOn - DateTime.UtcNow).TotalMilliseconds + 1000;
                CreateNotifyTask(retainerPtr, retainerNumber, delay);
                VentureWasCompleted[retainerNumber] = false;
            }

            if (showTime)
            {
                var ventureCompleteIn = ventureCompleteOn - DateTime.UtcNow;
                PrintChatMessage($"{retainerNames[retainerNumber]} will complete their Venture in {FormatTimeSpanString(ventureCompleteIn)}.");
            }
        }

        private void CreateNotifyTask(Retainer* retainerPtr, int retainerNumber, int delay, bool showTime = false)
        {
            CleanupOldTask(retainerNumber);
            cancelTokenPerRetainer[retainerNumber] = new CancellationTokenSource();
            var newTask = Task.Delay(delay).ContinueWith(t => SendChatEntry(retainerPtr, retainerNumber, showTime), cancelTokenPerRetainer[retainerNumber].Token);
            notifyTaskPerRetainer[retainerNumber] = newTask;
        }

        private void CleanupOldTasks()
        {
            for (int i = 0; i < currentRetainerCount; i++)
            {
                CleanupOldTask(i);
            }
        }

        private void CleanupOldTask(int retainerNumber)
        {
            if (notifyTaskPerRetainer[retainerNumber]?.IsCompleted is not null and false)
            {
                cancelTokenPerRetainer[retainerNumber].Cancel();
            }

            if (cancelTokenPerRetainer[retainerNumber] is not null)
            {
                cancelTokenPerRetainer[retainerNumber].Dispose();
            }
        }

        private void PrintChatMessage(string chatMsg)
        {
            var xivChat = new XivChatEntry()
            {
                Name = this.Name,
                Type = XivChatType.RetainerSale,
                Message = new SeString(new Payload[]
                {
                    new TextPayload(chatMsg)
                })
            };

            ChatGui.PrintChat(xivChat);
        }

        private string GetRetainerName(byte* namePtr)
        {
            byte[] retainerNameCharacters = new byte[20];
            byte character;
            int j;
            for (j = 0; j < 20; j++)
            {
                character = *(namePtr + j);
                if (character == 0)
                {
                    break;
                }

                retainerNameCharacters[j] = character;
            }

            return Encoding.ASCII.GetString(retainerNameCharacters).Substring(0, j);
        }

        private DateTime ConvertFromUnixTimestamp(uint timestamp)
        {
            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            return origin.AddSeconds(timestamp);
        }

        private static string FormatTimeSpanString(TimeSpan ventureCompleteIn)
        {
            string ventureCompleteInStr = "";

            if (ventureCompleteIn.Hours >= 1)
            {
                ventureCompleteInStr = $"{ventureCompleteIn.Hours}h";
            }
            if (ventureCompleteIn.Minutes >= 1)
            {
                ventureCompleteInStr += $"{ventureCompleteIn.Minutes}m";
            }

            ventureCompleteInStr += $"{ventureCompleteIn.Seconds}s";
            return ventureCompleteInStr;
        }

        public void Dispose()
        {
            CleanupOldTasks();

            Framework.Update -= OnFrameworkUpdate;
            //this.PluginUi.Dispose();
            CommandManager?.RemoveHandler(commandName);
        }

        private void OnCommand(string command, string args)
        {
            // in response to the slash command, just display our main ui
            //this.PluginUi.Visible = true;
        }

        private void DrawUI()
        {
            //this.PluginUi.Draw();
        }

        private void DrawConfigUI()
        {
            //this.PluginUi.SettingsVisible = true;
        }
    }
}
