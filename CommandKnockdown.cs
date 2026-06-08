using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/knockdown on|off|status</c> - lets a player opt their own character in or out of
    /// the knockdown system. The choice is persisted across sessions and server restarts
    /// (Knockdown.optout.xml). Opting out while currently downed kills the player immediately.
    ///
    /// Permission node: <c>knockdown.optout</c>. To let everyone use it, grant the node to the
    /// default group in Rocket/Permissions.config.xml.
    /// </summary>
    public sealed class CommandKnockdown : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "knockdown";
        public string Help => "Enable/disable the knockdown system for yourself.";
        public string Syntax => "<on|off|status>";
        public List<string> Aliases => new List<string> { "kd" };
        public List<string> Permissions => new List<string> { "knockdown.optout" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            UnturnedPlayer up = (UnturnedPlayer)caller;

            Knockdown plugin = Knockdown.Instance;
            if (plugin == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Plugin not loaded.", Color.red);
                return;
            }

            KnockdownConfiguration cfg = plugin.Configuration.Instance;
            if (!cfg.AllowPlayerOptOut)
            {
                UnturnedChat.Say(caller, "[Knockdown] Per-player opt-out is disabled on this server.", Color.red);
                return;
            }

            CSteamID id = up.CSteamID;
            string arg = command.Length > 0 ? command[0].Trim().ToLowerInvariant() : "status";

            switch (arg)
            {
                case "off":
                case "disable":
                    plugin.SetOptOut(id, true);
                    Say(up, cfg.MessageKnockdownDisabled,
                        "[Knockdown] Knockdown disabled for you — you will die normally.", Color.yellow);
                    break;

                case "on":
                case "enable":
                    plugin.SetOptOut(id, false);
                    Say(up, cfg.MessageKnockdownEnabled,
                        "[Knockdown] Knockdown enabled for you.", Color.green);
                    break;

                default: // "status" and anything unrecognised
                    bool isOff = plugin.IsOptedOut(id);
                    UnturnedChat.Say(caller,
                        isOff
                            ? "[Knockdown] Status: OFF (you die normally). Use /knockdown on to re-enable."
                            : "[Knockdown] Status: ON (you get downed). Use /knockdown off to disable.",
                        isOff ? Color.yellow : Color.green);
                    break;
            }
        }

        /// <summary>Sends a configured message if it has text; otherwise a plain fallback line.</summary>
        private static void Say(UnturnedPlayer up, Message msg, string fallback, Color fallbackColor)
        {
            if (msg != null && !string.IsNullOrEmpty(msg.Text))
            {
                Color color = UnturnedChat.GetColorFromName(msg.Color, fallbackColor);
                ChatManager.serverSendMessage(
                    msg.Text, color,
                    fromPlayer: null,
                    toPlayer: up.Player.channel.owner,
                    mode: EChatMode.SAY,
                    iconURL: null,
                    useRichTextFormatting: true);
            }
            else
            {
                UnturnedChat.Say(up, fallback, fallbackColor);
            }
        }
    }
}
