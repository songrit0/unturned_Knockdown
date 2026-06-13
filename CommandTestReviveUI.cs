using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace Knockdown
{
    /// <summary>
    /// <c>/testreviveui</c> - previews the center-screen revive HUD on yourself and animates the
    /// bar 0 -> 100% once, so the UI effect can be verified WITHOUT a second player to revive you.
    /// Requires ReviveUIEffectID to be set (the HUD bundle published/loaded).
    /// Permission node: <c>knockdown.test</c>.
    /// </summary>
    public sealed class CommandTestReviveUI : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "testreviveui";
        public string Help => "Preview the revive HUD on yourself (animates 0->100%).";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "reviveuitest", "rtest" };
        public List<string> Permissions => new List<string> { "knockdown.test" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (Knockdown.Instance == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Plugin not loaded.", Color.red);
                return;
            }

            UnturnedPlayer up = (UnturnedPlayer)caller;
            Player player = up.Player;
            if (player == null)
            {
                UnturnedChat.Say(caller, "[Knockdown] Invalid state.", Color.red);
                return;
            }

            if (Knockdown.Instance.Configuration.Instance.ReviveUIEffectID == 0)
            {
                UnturnedChat.Say(caller,
                    "[Knockdown] ReviveUIEffectID is 0 - set it to your HUD effect id (e.g. 30020) and restart first.",
                    Color.red);
                return;
            }

            if (Knockdown.Instance.StartReviveUIDemo(player))
                UnturnedChat.Say(caller, "[Knockdown] Showing revive HUD demo (0->100%)...", Color.yellow);
            else
                UnturnedChat.Say(caller, "[Knockdown] Could not start HUD demo.", Color.red);
        }
    }
}
